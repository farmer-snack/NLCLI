using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ClassIsland.Core;
using Cecil = Mono.Cecil;

namespace ClassIsland.Services.PluginCompatibility;

/// <summary>
/// 兼容层返回结果。
/// </summary>
/// <param name="AssemblyPath">
/// 改写后的程序集路径。为 <see langword="null"/> 时表示无需改写，调用方应继续使用原始路径。
/// </param>
/// <param name="Notes">改写过程的说明，用于在插件详情里展示兼容层做了什么。</param>
internal sealed record PluginCompatibilityResult(string? AssemblyPath, IReadOnlyList<string> Notes);

/// <summary>
/// 插件二进制兼容层：把按旧版 Avalonia（主版本低于宿主）编译的插件，在加载前重写其类型引用，
/// 使其能够绑定到宿主当前携带的 Avalonia / 控件库版本。
/// </summary>
/// <remarks>
/// <para>
/// 背景：ClassIsland 2.x 已从 Avalonia 11 升级到 Avalonia 12。插件包通常不携带 Avalonia 本体
/// （由宿主统一提供），所以插件元数据里记录的 <c>Avalonia.Controls, 11.x</c> 在运行时会被绑定到宿主的 12.x。
/// 程序集绑定本身没问题，但控件库里的部分类型在 12 里改了名 —— 例如 FluentAvalonia 3 给所有控件
/// 统一加了 <c>FA</c> 前缀（<c>ContentDialog</c> → <c>FAContentDialog</c>）——
/// 于是插件一加载就抛 <see cref="TypeLoadException"/>，表现为「插件装上了但启用不了」。
/// </para>
/// <para>
/// 做法：读取插件元数据，把「宿主里确实存在、但名字或所属程序集变了」的类型引用改写成新位置。
/// 映射关系<b>完全由宿主当前的实际类型表推导</b>，不含任何针对具体插件的硬编码，
/// 因此对所有按旧版 Avalonia 编译的插件都通用。
/// </para>
/// <para>
/// 已经是新版本 Avalonia（引用主版本 ≥ 宿主主版本）编译的插件<b>不会</b>经过本层，原样加载，
/// 避免把本来就能正常工作的插件改坏。
/// </para>
/// </remarks>
internal static class PluginTypeCompatibility
{
    /// <summary>
    /// 兼容层产物缓存目录。
    /// </summary>
    private static readonly string CacheRoot =
        Path.Combine(CommonDirectories.AppCacheFolderPath, "PluginCompatibility");

    /// <summary>
    /// 宿主当前 Avalonia 的主版本号。用于判断插件是否需要兼容处理。
    /// </summary>
    private static readonly Lazy<int> HostAvaloniaMajor = new(() =>
    {
        try
        {
            return typeof(Avalonia.Controls.Control).Assembly.GetName().Version?.Major ?? 0;
        }
        catch
        {
            return 0;
        }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 宿主全部程序集的类型表，用于推导映射关系。首次需要时构建。
    /// </summary>
    private static readonly Lazy<HostTypeIndex> HostTypes =
        new(HostTypeIndex.Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 判断并（在必要时）生成兼容改写后的程序集。
    /// </summary>
    /// <param name="assemblyPath">插件目录中的原始程序集路径。</param>
    /// <returns>兼容处理结果；<see cref="PluginCompatibilityResult.AssemblyPath"/> 为 <see langword="null"/> 表示保持原样。</returns>
    public static PluginCompatibilityResult Prepare(string assemblyPath)
    {
        var notes = new List<string>();
        try
        {
            if (!File.Exists(assemblyPath))
            {
                return new PluginCompatibilityResult(null, notes);
            }

            var pluginMajor = GetReferencedAvaloniaMajor(assemblyPath);
            var hostMajor = HostAvaloniaMajor.Value;

            // 不引用 Avalonia（纯逻辑插件），或插件本身就是新版本编译的 → 一律不动。
            if (pluginMajor is null || hostMajor <= 0 || pluginMajor >= hostMajor)
            {
                return new PluginCompatibilityResult(null, notes);
            }

            var cacheFile = GetCacheFilePath(assemblyPath, hostMajor);
            if (File.Exists(cacheFile))
            {
                notes.Add($"已按 Avalonia {pluginMajor} → {hostMajor} 兼容规则使用改写后的程序集（缓存命中）。");
                return new PluginCompatibilityResult(cacheFile, notes);
            }

            var changes = new List<string>();
            var warnings = new List<string>();
            if (!TryRewrite(assemblyPath, cacheFile, changes, warnings, out var failure))
            {
                if (!string.IsNullOrEmpty(failure))
                {
                    notes.Add(failure);
                }

                return new PluginCompatibilityResult(null, notes);
            }

            notes.Add($"检测到插件按 Avalonia {pluginMajor} 编译，宿主为 {hostMajor}，已启用类型重定向兼容层。");
            if (changes.Count > 0)
            {
                notes.Add("已重定向的类型：" + string.Join("、", changes.Take(12)));
                if (changes.Count > 12)
                {
                    notes.Add($"其余 {changes.Count - 12} 处类型重定向已省略。");
                }
            }

            foreach (var warning in warnings)
            {
                notes.Add(warning);
            }

            return new PluginCompatibilityResult(cacheFile, notes);
        }
        catch (Exception ex)
        {
            notes.Add($"兼容层处理失败，将按原样加载：{ex.Message}");
            return new PluginCompatibilityResult(null, notes);
        }
    }

    /// <summary>
    /// 读取程序集引用的 Avalonia 主版本。不引用 Avalonia 时返回 <see langword="null"/>。
    /// </summary>
    private static int? GetReferencedAvaloniaMajor(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            return null;
        }

        var reader = peReader.GetMetadataReader();
        var major = -1;
        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            var name = reader.GetString(reference.Name);
            if (!IsAvaloniaAssembly(name))
            {
                continue;
            }

            major = Math.Max(major, reference.Version.Major);
        }

        return major < 0 ? null : major;
    }

    /// <summary>
    /// 判断程序集名是否属于 Avalonia 本体。
    /// </summary>
    private static bool IsAvaloniaAssembly(string name) =>
        name.Equals("Avalonia", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Avalonia.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 计算缓存文件路径。缓存键包含源文件内容与宿主主版本，插件或宿主更新后会自动失效。
    /// </summary>
    private static string GetCacheFilePath(string assemblyPath, int hostMajor)
    {
        string hash;
        using (var stream = File.OpenRead(assemblyPath))
        {
            hash = Convert.ToHexString(SHA256.HashData(stream))[..16];
        }

        var directory = Path.Combine(CacheRoot, $"{hash}-v{hostMajor}");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Path.GetFileName(assemblyPath));
    }

    /// <summary>
    /// 执行类型引用重写并写出到 <paramref name="targetPath"/>。
    /// </summary>
    private static bool TryRewrite(
        string sourcePath,
        string targetPath,
        List<string> changes,
        List<string> warnings,
        out string? failure)
    {
        failure = null;
        Cecil.AssemblyDefinition assembly;
        try
        {
            assembly = Cecil.AssemblyDefinition.ReadAssembly(sourcePath, new Cecil.ReaderParameters { ReadSymbols = false });
        }
        catch (Exception ex)
        {
            failure = $"无法读取插件程序集元数据：{ex.Message}";
            return false;
        }

        using (assembly)
        {
            var module = assembly.MainModule;

            // 强名称程序集改写后签名必然失效，且无法在不持有私钥的情况下重新签名。
            // 但 .NET（Core）运行时默认不校验强名称签名，去掉签名标记后照样能加载，
            // 所以这里直接摘掉签名继续改写，而不是把这类插件整体放弃。
            if (assembly.Name.HasPublicKey)
            {
                assembly.Name.HasPublicKey = false;
                assembly.Name.PublicKey = [];
                module.Attributes &= ~Cecil.ModuleAttributes.StrongNameSigned;
                changes.Add("已移除程序集的强名称签名标记（.NET 运行时不校验签名）。");
            }
            var index = HostTypes.Value;
            var unresolved = new List<string>();

            foreach (var reference in module.GetTypeReferences().ToList())
            {
                if (reference.Scope is not Cecil.AssemblyNameReference scope)
                {
                    continue;
                }

                if (HostTypeIndex.IsFrameworkAssembly(scope.Name))
                {
                    continue;
                }

                var ns = reference.Namespace ?? "";
                // 已经是嵌套类型引用时，全名形如 Ns.Outer/Inner，直接拼 ns + name 会丢掉外层类型。
                var fullName = reference.DeclaringType != null
                    ? reference.FullName
                    : string.IsNullOrEmpty(ns) ? reference.Name : ns + "." + reference.Name;

                // 当前程序集里就有这个类型 → 无需处理。
                if (index.TryGetInAssembly(scope.Name, fullName))
                {
                    continue;
                }

                // 规则 A：类型被搬到了宿主里的另一个程序集（同全名）。
                if (index.TryGetOwners(fullName, out var owners))
                {
                    var destination = owners.FirstOrDefault(x => !HostTypeIndex.IsFrameworkAssembly(x)) ?? owners[0];
                    if (!destination.Equals(scope.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        reference.Scope = ResolveAssemblyReference(module, destination);
                        changes.Add($"{scope.Name} → {destination}：{fullName}");
                    }

                    continue;
                }

                // 规则 A2：宿主把原本独立的类型收进了某个外层类型，或换了外层类型。
                // 例如 ClassIsland.Core.Controls.DrawerPlacementEnum 在新版本里变成 DrawerHost 的嵌套类型。
                // 这类变化无法靠全名匹配发现，只能按「简单名唯一 + 命名空间相容」推断，
                // 因此只在引用目标是 UI 库或宿主自身程序集时启用，避免误伤插件的私有类型。
                var namespaceForMatch = reference.DeclaringType?.Namespace is { Length: > 0 } outerNs
                    ? outerNs
                    : ns;
                if (HostTypeIndex.IsUiOrHostAssembly(scope.Name) &&
                    index.TryGetUniqueNested(reference.Name, namespaceForMatch, out var nested) &&
                    (reference.DeclaringType == null ||
                     !reference.DeclaringType.Name.Equals(nested.OuterName, StringComparison.Ordinal) ||
                     !reference.DeclaringType.Namespace.Equals(nested.OuterNamespace, StringComparison.Ordinal)))
                {
                    var outerScope = ResolveAssemblyReference(module, nested.OuterAssembly);
                    reference.DeclaringType = new Cecil.TypeReference(nested.OuterNamespace, nested.OuterName, module, outerScope);
                    reference.Namespace = "";
                    changes.Add($"{scope.Name}::{fullName} → {nested.OuterNamespace}.{nested.OuterName}/{reference.Name}");
                    continue;
                }

                // 规则 B：控件库统一改名（FluentAvalonia 3 为所有控件加 FA 前缀）。
                // 只对 UI 库 / 宿主自身程序集启用，避免把插件的第三方库中恰好叫 FAxxx 的类型误判。
                var prefixedName = string.IsNullOrEmpty(ns) ? "FA" + reference.Name : ns + ".FA" + reference.Name;
                if (HostTypeIndex.IsUiOrHostAssembly(scope.Name) &&
                    index.TryGetOwners(prefixedName, out var prefixedOwners))
                {
                    reference.Name = "FA" + reference.Name;
                    var destination = prefixedOwners[0];
                    if (!destination.Equals(scope.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        reference.Scope = ResolveAssemblyReference(module, destination);
                    }

                    changes.Add($"{scope.Name}::{fullName} → {prefixedName}");
                    continue;
                }

                // 规则 C：宿主自己的 API 在重构中调整了命名空间（例如 ClassIsland.Core.HandleDelegate
                // 被移到全局命名空间）。只在「目标是宿主自家程序集」且「同名类型在宿主里唯一」时生效，
                // 避免把 Rectangle、Path 这类通用名字误映射到无关类型上。
                if (HostTypeIndex.IsHostOwnedAssembly(scope.Name) &&
                    index.TryGetUniqueBySimpleName(reference.Name, out var targetNamespace, out var targetAssembly))
                {
                    // 原来的引用可能是某个外层类型的嵌套类型，而宿主里它已经变成顶层类型，
                    // 这时必须清掉外层，否则会写出「嵌套在不存在的外层下」的非法引用。
                    reference.DeclaringType = null;
                    reference.Namespace = targetNamespace;
                    if (!targetAssembly.Equals(scope.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        reference.Scope = ResolveAssemblyReference(module, targetAssembly);
                    }

                    changes.Add($"{scope.Name}::{fullName} → {(string.IsNullOrEmpty(targetNamespace) ? targetAssembly : targetNamespace + "." + reference.Name)}");
                    continue;
                }

                // 规则 D：宿主里确实没有 → 记录但不阻断，交给运行时按需报错。
                unresolved.Add(fullName);
            }

            try
            {
                assembly.Write(targetPath);
            }
            catch (Exception ex)
            {
                failure = $"写出兼容程序集失败：{ex.Message}";
                return false;
            }

            if (changes.Count == 0)
            {
                // 什么都没改，缓存文件没有意义，删掉避免占用空间。
                TryDelete(targetPath);
                failure = unresolved.Count == 0
                    ? null
                    : $"插件引用了宿主中不存在的类型（{unresolved.Count} 个），无法通过兼容层修复：{FormatList(unresolved)}";
                return false;
            }

            if (unresolved.Count > 0)
            {
                warnings.Add($"仍有 {unresolved.Count} 个类型在宿主中找不到对应（{FormatList(unresolved)}），相关功能可能不可用。");
            }

            return true;
        }
    }

    /// <summary>
    /// 取得目标程序集在模块里的引用；不存在时按宿主实际版本补一个。
    /// </summary>
    private static Cecil.AssemblyNameReference ResolveAssemblyReference(Cecil.ModuleDefinition module, string assemblyName)
    {
        var existing = module.AssemblyReferences.FirstOrDefault(x =>
            x.Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            return existing;
        }

        Version version;
        byte[]? token = null;
        try
        {
            var hostAssembly = AppDomain.CurrentDomain.GetAssemblies()
                                   .FirstOrDefault(x => x.GetName().Name?.Equals(assemblyName, StringComparison.OrdinalIgnoreCase) == true)
                               ?? Assembly.Load(new AssemblyName(assemblyName));
            version = hostAssembly.GetName().Version ?? new Version(1, 0, 0, 0);
            token = hostAssembly.GetName().GetPublicKeyToken();
        }
        catch
        {
            version = new Version(1, 0, 0, 0);
        }

        var created = new Cecil.AssemblyNameReference(assemblyName, version);
        if (token is { Length: > 0 })
        {
            created.PublicKeyToken = token;
        }

        module.AssemblyReferences.Add(created);
        return created;
    }

    /// <summary>
    /// 把类型列表压缩成一条可读的说明，避免日志被大量类型名淹没。
    /// </summary>
    private static string FormatList(List<string> items, int limit = 6)
    {
        var head = string.Join("、", items.Take(limit));
        return items.Count > limit ? $"{head} 等 {items.Count} 个" : head;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略清理失败。
        }
    }

    /// <summary>
    /// 宿主程序集的类型索引。
    /// </summary>
    private sealed class HostTypeIndex
    {
        private static readonly string[] FrameworkPrefixes =
        [
            "System",
            "netstandard",
            "mscorlib",
            "WindowsBase",
            "Microsoft.Win32.Primitives",
            "Microsoft.CSharp",
            "Microsoft.VisualBasic"
        ];

        private readonly Dictionary<string, HashSet<string>> _byAssembly =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<string>> _byFullName = new(StringComparer.Ordinal);

        private readonly Dictionary<string, List<(string Namespace, string Assembly)>> _bySimpleName =
            new(StringComparer.Ordinal);

        /// <summary>
        /// 「简单名 → 嵌套类型」索引。用于识别宿主把原本独立的类型收进某个外层类型的情况。
        /// 只收录 UI 库与宿主自身程序集，且要求外层类型是顶层类型。
        /// </summary>
        private readonly Dictionary<string, List<NestedTypeInfo>> _nestedBySimpleName =
            new(StringComparer.Ordinal);

        public static bool IsFrameworkAssembly(string name) =>
            FrameworkPrefixes.Any(prefix =>
                name.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 判断程序集是否属于 ClassIsland 自身。只有自家的 API 才允许按「同名唯一」推断命名空间变化。
        /// </summary>
        public static bool IsHostOwnedAssembly(string name) =>
            name.Equals("ClassIsland", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ClassIsland.", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 判断程序集是否属于「UI 库或宿主自身」。类型改名/嵌套化的推断只在这类程序集上启用，
        /// 避免把插件自己的类型按简单名误配到宿主里。
        /// </summary>
        public static bool IsUiOrHostAssembly(string name) =>
            IsHostOwnedAssembly(name) ||
            name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("FluentAvalonia", StringComparison.OrdinalIgnoreCase);

        public bool TryGetInAssembly(string assemblyName, string fullName) =>
            _byAssembly.TryGetValue(assemblyName, out var set) && set.Contains(fullName);

        public bool TryGetOwners(string fullName, out List<string> owners)
        {
            if (_byFullName.TryGetValue(fullName, out var list) && list.Count > 0)
            {
                owners = list;
                return true;
            }

            owners = [];
            return false;
        }

        /// <summary>
        /// 按「简单名」查找宿主里的唯一嵌套类型。命名空间必须相容，存在多个候选时判定为不唯一。
        /// </summary>
        public bool TryGetUniqueNested(string simpleName, string referenceNamespace, out NestedTypeInfo target)
        {
            if (_nestedBySimpleName.TryGetValue(simpleName, out var list))
            {
                var compatible = list
                    .Where(x => NamespaceCompatible(referenceNamespace, x.OuterNamespace))
                    .ToList();
                if (compatible.Count == 1)
                {
                    target = compatible[0];
                    return true;
                }
            }

            target = default;
            return false;
        }

        /// <summary>
        /// 判断两个命名空间是否「同源」：相同，或一方是另一方的上级命名空间。
        /// </summary>
        private static bool NamespaceCompatible(string left, string right)
        {
            if (string.Equals(left, right, StringComparison.Ordinal))
            {
                return true;
            }

            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            return left.StartsWith(right + ".", StringComparison.Ordinal) ||
                   right.StartsWith(left + ".", StringComparison.Ordinal);
        }

        /// <summary>
        /// 按「简单名」查找宿主里的唯一顶层类型。存在多个同名类型时判定为不唯一，返回 <see langword="false"/>。
        /// </summary>
        public bool TryGetUniqueBySimpleName(string simpleName, out string targetNamespace, out string targetAssembly)
        {
            if (_bySimpleName.TryGetValue(simpleName, out var list) && list.Count == 1)
            {
                targetNamespace = list[0].Namespace;
                targetAssembly = list[0].Assembly;
                return true;
            }

            targetNamespace = "";
            targetAssembly = "";
            return false;
        }

        /// <summary>
        /// 扫描宿主目录下所有程序集，建立「类型全名 → 所属程序集」的反向索引。
        /// </summary>
        public static HostTypeIndex Build()
        {
            var index = new HostTypeIndex();
            var directory = AppContext.BaseDirectory;

            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).ToList();
            }
            catch
            {
                return index;
            }

            foreach (var file in files)
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    using var peReader = new PEReader(stream);
                    if (!peReader.HasMetadata)
                    {
                        continue;
                    }

                    var reader = peReader.GetMetadataReader();
                    if (!reader.IsAssembly)
                    {
                        continue;
                    }

                    var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);

                    // 先收集全部类型定义，便于定位嵌套类型的外层类型。
                    var definitions = new Dictionary<TypeDefinitionHandle, (string Namespace, string Name)>();
                    foreach (var handle in reader.TypeDefinitions)
                    {
                        var definition = reader.GetTypeDefinition(handle);
                        definitions[handle] = (reader.GetString(definition.Namespace), reader.GetString(definition.Name));
                    }

                    foreach (var (handle, info) in definitions)
                    {
                        var declaringType = reader.GetTypeDefinition(handle).GetDeclaringType();
                        if (declaringType.IsNil)
                        {
                            // 顶层类型按全名索引。
                            index.Add(assemblyName, info.Namespace, info.Name);
                            continue;
                        }

                        if (!definitions.TryGetValue(declaringType, out var outer))
                        {
                            continue;
                        }

                        // 嵌套类型的完整全名（Outer/Inner）也要进入全名索引，
                        // 否则插件里原本合法的嵌套引用会被误判成「宿主中不存在」。
                        index.AddNestedFullName(assemblyName, outer.Namespace, outer.Name, info.Name);
                    }

                    // 嵌套类型只在 UI 库 / 宿主自身程序集里建立「简单名」索引。
                    // 其它程序集（尤其是 System.*）同名嵌套类型太多，按简单名匹配极易出错。
                    if (!IsUiOrHostAssembly(assemblyName))
                    {
                        continue;
                    }

                    foreach (var (handle, info) in definitions)
                    {
                        var declaringType = reader.GetTypeDefinition(handle).GetDeclaringType();
                        if (declaringType.IsNil || !definitions.TryGetValue(declaringType, out var outer))
                        {
                            continue;
                        }

                        index.AddNested(assemblyName, outer.Namespace, outer.Name, info.Name);
                    }
                }
                catch
                {
                    // 非托管 dll / 损坏文件，跳过。
                }
            }

            return index;
        }

        private void Add(string assemblyName, string ns, string name)
        {
            var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

            if (!_byAssembly.TryGetValue(assemblyName, out var set))
            {
                _byAssembly[assemblyName] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(fullName);

            if (!_byFullName.TryGetValue(fullName, out var owners))
            {
                _byFullName[fullName] = owners = [];
            }

            if (!owners.Contains(assemblyName, StringComparer.OrdinalIgnoreCase))
            {
                owners.Add(assemblyName);
            }

            if (!_bySimpleName.TryGetValue(name, out var simple))
            {
                _bySimpleName[name] = simple = [];
            }

            if (!simple.Any(x => x.Assembly.Equals(assemblyName, StringComparison.OrdinalIgnoreCase) &&
                                 x.Namespace.Equals(ns, StringComparison.Ordinal)))
            {
                simple.Add((ns, assemblyName));
            }
        }

        /// <summary>
        /// 记录嵌套类型的完整全名（<c>Ns.Outer/Inner</c>），用于精确匹配已有的嵌套引用。
        /// </summary>
        private void AddNestedFullName(string assemblyName, string outerNamespace, string outerName, string innerName)
        {
            var outerFullName = string.IsNullOrEmpty(outerNamespace) ? outerName : outerNamespace + "." + outerName;
            var fullName = outerFullName + "/" + innerName;

            if (!_byAssembly.TryGetValue(assemblyName, out var set))
            {
                _byAssembly[assemblyName] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(fullName);

            if (!_byFullName.TryGetValue(fullName, out var owners))
            {
                _byFullName[fullName] = owners = [];
            }

            if (!owners.Contains(assemblyName, StringComparer.OrdinalIgnoreCase))
            {
                owners.Add(assemblyName);
            }
        }

        /// <summary>
        /// 记录一个嵌套类型（<c>Outer+Inner</c>），供「原本独立的类型被收进外层类型」的场景使用。
        /// </summary>
        private void AddNested(string assemblyName, string outerNamespace, string outerName, string innerName)
        {
            if (!_nestedBySimpleName.TryGetValue(innerName, out var list))
            {
                _nestedBySimpleName[innerName] = list = [];
            }

            if (!list.Any(x => x.OuterAssembly.Equals(assemblyName, StringComparison.OrdinalIgnoreCase) &&
                               x.OuterNamespace.Equals(outerNamespace, StringComparison.Ordinal) &&
                               x.OuterName.Equals(outerName, StringComparison.Ordinal)))
            {
                list.Add(new NestedTypeInfo(outerNamespace, outerName, innerName, assemblyName));
            }
        }

        /// <summary>
        /// 宿主里的嵌套类型信息。
        /// </summary>
        public readonly record struct NestedTypeInfo(
            string OuterNamespace,
            string OuterName,
            string InnerName,
            string OuterAssembly);
    }
}
