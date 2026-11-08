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
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using ClassIsland.Core;
using ClassIsland.Core.Compatibility;
using Cecil = Mono.Cecil;
using CecilIL = Mono.Cecil.Cil;

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
    /// 兼容规则的版本号。重写逻辑发生变化时必须递增，否则旧缓存会被继续复用。
    /// </summary>
    private const int CompatibilityVersion = 3;

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
    /// Avalonia 12 中已经彻底移除、只能改名到新类型的旧类型。
    /// 这些名字无法从宿主类型表里反推（新旧名字没有包含关系），因此显式列出。
    /// </summary>
    private static readonly Dictionary<string, (string Namespace, string Name, string Assembly)> RemovedTypeAliases =
        new(StringComparer.Ordinal)
        {
            ["Avalonia.Data.IBinding"] = ("Avalonia.Data", "BindingBase", "Avalonia.Base"),
            ["Avalonia.Controls.SystemDecorations"] = ("Avalonia.Controls", "WindowDecorations", "Avalonia.Controls"),
            ["Avalonia.Input.IDataObject"] = ("Avalonia.Input", "IDataTransfer", "Avalonia.Base"),
            ["Avalonia.Input.DataObject"] = ("Avalonia.Input", "DataTransfer", "Avalonia.Base"),
        };


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

        var directory = Path.Combine(CacheRoot, $"{hash}-v{hostMajor}-c{CompatibilityVersion}");
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
        using var resolver = new Cecil.DefaultAssemblyResolver();
        resolver.AddSearchDirectory(AppContext.BaseDirectory);
        var pluginDirectory = Path.GetDirectoryName(sourcePath);
        if (!string.IsNullOrEmpty(pluginDirectory))
        {
            resolver.AddSearchDirectory(pluginDirectory);
        }

        try
        {
            assembly = Cecil.AssemblyDefinition.ReadAssembly(sourcePath, new Cecil.ReaderParameters
            {
                ReadSymbols = false,
                AssemblyResolver = resolver
            });
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
            RewriteMembers(module, changes);
            ApplyTypeAliases(module, changes);
            FixInstructionOperands(module, changes);

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
    /// 重写插件里对 Avalonia 11 成员的引用。类型别名由 <see cref="ApplyTypeAliases"/> 统一处理，
    /// 这里只处理签名或调用形态发生变化的成员。
    /// </summary>
    private static void RewriteMembers(Cecil.ModuleDefinition module, List<string> changes)
    {
        foreach (var member in module.GetMemberReferences().ToList())
        {
            switch (member)
            {
                case Cecil.MethodReference method:
                    RewriteMethod(module, method, changes);
                    break;
                case Cecil.FieldReference field:
                    RewriteField(module, field, changes);
                    break;
            }
        }
    }

    /// <summary>
    /// 重写单个方法引用。映射只针对 Avalonia 12 中确实发生变化、且无法靠类型别名解决的成员。
    /// </summary>
    private static void RewriteMethod(Cecil.ModuleDefinition module, Cecil.MethodReference method, List<string> changes)
    {
        // 泛型方法调用在元数据里是 MethodSpecification，它本身不允许改 DeclaringType 等成员，
        // 必须改它指向的 ElementMethod。
        if (method is Cecil.MethodSpecification specification)
        {
            method = specification.ElementMethod;
        }

        if (method.DeclaringType == null)
        {
            return;
        }

        var declaring = GetElementTypeFullName(method.DeclaringType);
        var name = method.Name;
        var parameterCount = method.Parameters.Count;

        // Profile 的集合属性已改为 ObservableOrderedDictionary<TKey, TValue>（派生自原类型）。
        if (declaring.Equals("ClassIsland.Shared.Models.Profile.Profile", StringComparison.Ordinal) &&
            parameterCount == 0 &&
            name is "get_ClassPlans" or "get_TimeLayouts" or "get_Subjects")
        {
            if (TryMakeOrderedDictionary(module, method.ReturnType, out var ordered))
            {
                method.ReturnType = ordered;
                changes.Add($"{declaring}::{name} 返回类型 → ObservableOrderedDictionary");
            }

            return;
        }

        // XAML 扩展在 Avalonia 12 中返回 BindingBase / ReflectionBinding。
        if (declaring == "Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension" &&
            name == "ProvideValue")
        {
            method.ReturnType = MakeTypeReference(module, "Avalonia.Data", "BindingBase", "Avalonia.Base");
            return;
        }

        if (declaring == "Avalonia.Markup.Xaml.MarkupExtensions.ReflectionBindingExtension" &&
            name == "ProvideValue")
        {
            method.ReturnType = MakeTypeReference(module, "Avalonia.Data", "ReflectionBinding", "Avalonia.Base");
            return;
        }

        if (declaring == "Avalonia.Data.TemplateBinding" && name == "ProvideValue")
        {
            method.ReturnType = MakeTypeReference(module, "Avalonia.Data", "BindingBase", "Avalonia.Base");
            return;
        }

        // IBinding 已并入 BindingBase。
        if (declaring == "Avalonia.Controls.DataGridBoundColumn" && name is "get_Binding" or "set_Binding")
        {
            if (name == "get_Binding")
            {
                method.ReturnType = MakeTypeReference(module, "Avalonia.Data", "BindingBase", "Avalonia.Base");
            }
            else if (parameterCount == 1)
            {
                method.Parameters[0].ParameterType =
                    MakeTypeReference(module, "Avalonia.Data", "BindingBase", "Avalonia.Base");
            }

            return;
        }

        if (declaring == "Avalonia.AvaloniaObjectExtensions" && name == "ToBinding" && parameterCount == 1)
        {
            method.ReturnType = MakeTypeReference(module, "Avalonia.Data", "BindingBase", "Avalonia.Base");
            return;
        }

        if (declaring == "Avalonia.AvaloniaObject" &&
            parameterCount == 2 &&
            name is "Bind" or "set_Item" &&
            GetElementTypeFullName(method.Parameters[1].ParameterType) == "Avalonia.Data.IBinding")
        {
            method.Parameters[1].ParameterType =
                MakeTypeReference(module, "Avalonia.Data", "BindingBase", "Avalonia.Base");
            changes.Add($"{declaring}::{name} 的绑定参数 → BindingBase");
            return;
        }

        // 静态扩展 Bind 在 Avalonia 12 中已移除，转到宿主垫片。
        if (declaring == "Avalonia.AvaloniaObjectExtensions" && name == "Bind" && parameterCount == 4)
        {
            method.DeclaringType = ShimType(module);
            method.HasThis = false;
            method.Parameters[2].ParameterType =
                MakeTypeReference(module, "Avalonia.Data", "BindingBase", "Avalonia.Base");
            changes.Add($"{declaring}::Bind → Avalonia12Compatibility.Bind");
            return;
        }

        // Avalonia 12 没有 Binding(string, BindingMode) 构造函数。
        if (declaring == "Avalonia.Data.Binding" &&
            name == ".ctor" &&
            parameterCount == 2 &&
            GetElementTypeFullName(method.Parameters[0].ParameterType) == "System.String" &&
            GetElementTypeFullName(method.Parameters[1].ParameterType) == "Avalonia.Data.BindingMode")
        {
            method.DeclaringType = ShimType(module);
            method.Name = "CreateBinding";
            method.HasThis = false;
            method.ReturnType = MakeTypeReference(module, "Avalonia.Data", "Binding", "Avalonia.Markup");
            changes.Add("Binding(string, BindingMode) → Avalonia12Compatibility.CreateBinding");
            return;
        }

        // Mode 属性在 Avalonia 12 中下移到 ReflectionBinding 上。
        if (declaring == "Avalonia.Data.BindingBase" && name == "set_Mode" && parameterCount == 1)
        {
            method.DeclaringType = MakeTypeReference(module, "Avalonia.Data", "ReflectionBinding", "Avalonia.Base");
            changes.Add("BindingBase.Mode → ReflectionBinding.Mode");
            return;
        }

        // 拖放数据 API：IDataObject / DataObject → IDataTransfer / DataTransfer。
        if (declaring == "Avalonia.Input.DragEventArgs" && name == "get_Data" && parameterCount == 0)
        {
            method.Name = "get_DataTransfer";
            method.ReturnType = MakeTypeReference(module, "Avalonia.Input", "IDataTransfer", "Avalonia.Base");
            changes.Add("DragEventArgs.Data → DragEventArgs.DataTransfer");
            return;
        }

        if (declaring == "Avalonia.Input.IDataObject" && name is "Contains" or "Get")
        {
            RetargetToShim(module, method, name, "Avalonia.Input", "IDataTransfer", "Avalonia.Base");
            changes.Add($"IDataObject.{name} → Avalonia12Compatibility.{name}");
            return;
        }

        if (declaring == "Avalonia.Input.DataObject" && name == "Set" && parameterCount == 2)
        {
            RetargetToShim(module, method, "Set", "Avalonia.Input", "DataTransfer", "Avalonia.Base");
            changes.Add("DataObject.Set → Avalonia12Compatibility.Set");
            return;
        }

        if (declaring == "Avalonia.Input.DragDrop" && name == "DoDragDrop" && parameterCount == 3)
        {
            method.Name = "DoDragDropAsync";
            method.Parameters[1].ParameterType =
                MakeTypeReference(module, "Avalonia.Input", "IDataTransfer", "Avalonia.Base");
            changes.Add("DragDrop.DoDragDrop → DragDrop.DoDragDropAsync");
            return;
        }

        if (declaring == "Avalonia.Controls.Window" &&
            name == "set_SystemDecorations" &&
            parameterCount == 1 &&
            GetElementTypeFullName(method.Parameters[0].ParameterType) == "Avalonia.Controls.SystemDecorations")
        {
            method.Parameters[0].ParameterType =
                MakeTypeReference(module, "Avalonia.Controls", "WindowDecorations", "Avalonia.Controls");
            changes.Add("Window.SystemDecorations → WindowDecorations");
            return;
        }

        // 剪贴板文本 API 在 Avalonia 12 中改为扩展方法。
        if (declaring == "Avalonia.Input.Platform.IClipboard" && name is "SetTextAsync" or "GetTextAsync")
        {
            method.DeclaringType =
                MakeTypeReference(module, "Avalonia.Input.Platform", "ClipboardExtensions", "Avalonia.Base");
            method.HasThis = false;
            method.Parameters.Insert(0, new Cecil.ParameterDefinition(
                MakeTypeReference(module, "Avalonia.Input.Platform", "IClipboard", "Avalonia.Base")));
            if (name == "GetTextAsync")
            {
                method.Name = "TryGetTextAsync";
            }

            changes.Add($"IClipboard.{name} → ClipboardExtensions");
        }
    }

    /// <summary>
    /// 重写字段引用的类型。字段本身仍然存在，只是类型变了。
    /// </summary>
    private static void RewriteField(Cecil.ModuleDefinition module, Cecil.FieldReference field, List<string> changes)
    {
        if (field.DeclaringType == null)
        {
            return;
        }

        var declaring = GetElementTypeFullName(field.DeclaringType);
        if (declaring == "Avalonia.Controls.Window" && field.Name == "WindowStateProperty")
        {
            var window = MakeTypeReference(module, "Avalonia.Controls", "Window", "Avalonia.Controls");
            var state = MakeTypeReference(module, "Avalonia.Controls", "WindowState", "Avalonia.Controls");
            field.FieldType = MakeGenericType(module, "Avalonia", "DirectProperty`2", "Avalonia.Base", window, state);
            changes.Add("Window.WindowStateProperty → DirectProperty<Window, WindowState>");
        }
    }

    /// <summary>
    /// 把实例方法的目标改为宿主垫片上的静态方法，并把原实例补成第一个参数。
    /// </summary>
    private static void RetargetToShim(
        Cecil.ModuleDefinition module,
        Cecil.MethodReference method,
        string name,
        string instanceNamespace,
        string instanceType,
        string instanceAssembly)
    {
        method.Name = name;
        method.DeclaringType = ShimType(module);
        method.HasThis = false;
        method.Parameters.Insert(0, new Cecil.ParameterDefinition(
            MakeTypeReference(module, instanceNamespace, instanceType, instanceAssembly)));
    }

    /// <summary>
    /// 把 <c>ObservableDictionary&lt;TKey, TValue&gt;</c> 的返回类型换成派生类型
    /// <c>ObservableOrderedDictionary&lt;TKey, TValue&gt;</c>，泛型参数保持不变。
    /// </summary>
    private static bool TryMakeOrderedDictionary(
        Cecil.ModuleDefinition module,
        Cecil.TypeReference type,
        out Cecil.TypeReference result)
    {
        if (type is Cecil.GenericInstanceType generic && generic.GenericArguments.Count == 2)
        {
            var scope = generic.ElementType.Scope ?? ResolveAssemblyReference(module, "ClassIsland.Shared");
            var element = new Cecil.TypeReference(
                "ClassIsland.Shared.ComponentModels",
                "ObservableOrderedDictionary`2",
                module,
                scope);
            var replacement = new Cecil.GenericInstanceType(element);
            foreach (var argument in generic.GenericArguments)
            {
                replacement.GenericArguments.Add(argument);
            }

            result = replacement;
            return true;
        }

        result = type;
        return false;
    }

    /// <summary>
    /// 对全部可达的类型引用应用 Avalonia 12 类型别名，包括泛型实参、数组、签名和 IL 里的类型操作数。
    /// </summary>
    private static void ApplyTypeAliases(Cecil.ModuleDefinition module, List<string> changes)
    {
        foreach (var reference in module.GetTypeReferences().ToList())
        {
            NormalizeType(module, reference, changes);
        }

        foreach (var member in module.GetMemberReferences().ToList())
        {
            switch (member)
            {
                case Cecil.GenericInstanceMethod genericMethod:
                    NormalizeMethodSignature(module, genericMethod.ElementMethod, changes);
                    for (var i = 0; i < genericMethod.GenericArguments.Count; i++)
                    {
                        genericMethod.GenericArguments[i] =
                            NormalizeType(module, genericMethod.GenericArguments[i], changes);
                    }

                    break;
                case Cecil.MethodReference method:
                    NormalizeMethodSignature(module, method, changes);
                    break;
                case Cecil.FieldReference field:
                    if (field.DeclaringType != null)
                    {
                        field.DeclaringType = NormalizeType(module, field.DeclaringType, changes);
                    }

                    field.FieldType = NormalizeType(module, field.FieldType, changes);
                    break;
            }
        }

        foreach (var type in module.GetTypes())
        {
            if (type.BaseType != null)
            {
                type.BaseType = NormalizeType(module, type.BaseType, changes);
            }

            for (var i = 0; i < type.Interfaces.Count; i++)
            {
                type.Interfaces[i] = new Cecil.InterfaceImplementation(
                    NormalizeType(module, type.Interfaces[i].InterfaceType, changes));
            }

            foreach (var field in type.Fields)
            {
                field.FieldType = NormalizeType(module, field.FieldType, changes);
            }

            foreach (var method in type.Methods)
            {
                method.ReturnType = NormalizeType(module, method.ReturnType, changes);
                foreach (var parameter in method.Parameters)
                {
                    parameter.ParameterType = NormalizeType(module, parameter.ParameterType, changes);
                }

                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var variable in method.Body.Variables)
                {
                    variable.VariableType = NormalizeType(module, variable.VariableType, changes);
                }

                foreach (var handler in method.Body.ExceptionHandlers)
                {
                    if (handler.CatchType != null)
                    {
                        handler.CatchType = NormalizeType(module, handler.CatchType, changes);
                    }
                }

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is Cecil.TypeReference operand)
                    {
                        instruction.Operand = NormalizeType(module, operand, changes);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 规范化一个方法引用的声明类型、返回类型和参数类型。
    /// </summary>
    private static void NormalizeMethodSignature(
        Cecil.ModuleDefinition module,
        Cecil.MethodReference method,
        List<string> changes)
    {
        if (method.DeclaringType != null)
        {
            method.DeclaringType = NormalizeType(module, method.DeclaringType, changes);
        }

        method.ReturnType = NormalizeType(module, method.ReturnType, changes);
        foreach (var parameter in method.Parameters)
        {
            parameter.ParameterType = NormalizeType(module, parameter.ParameterType, changes);
        }
    }

    /// <summary>
    /// 递归处理类型引用，把已移除的旧类型替换成宿主中的新类型。
    /// </summary>
    private static Cecil.TypeReference NormalizeType(
        Cecil.ModuleDefinition module,
        Cecil.TypeReference type,
        List<string> changes)
    {
        switch (type)
        {
            case Cecil.GenericParameter:
                return type;
            case Cecil.GenericInstanceType generic:
                NormalizeType(module, generic.ElementType, changes);
                foreach (var argument in generic.GenericArguments)
                {
                    NormalizeType(module, argument, changes);
                }

                return generic;
            case Cecil.ArrayType array:
                NormalizeType(module, array.ElementType, changes);
                return array;
            case Cecil.ByReferenceType byReference:
                NormalizeType(module, byReference.ElementType, changes);
                return byReference;
            case Cecil.PointerType pointer:
                NormalizeType(module, pointer.ElementType, changes);
                return pointer;
            case Cecil.PinnedType pinned:
                NormalizeType(module, pinned.ElementType, changes);
                return pinned;
            case Cecil.OptionalModifierType optional:
                NormalizeType(module, optional.ElementType, changes);
                NormalizeType(module, optional.ModifierType, changes);
                return optional;
            case Cecil.RequiredModifierType required:
                NormalizeType(module, required.ElementType, changes);
                NormalizeType(module, required.ModifierType, changes);
                return required;
            case Cecil.TypeSpecification:
                return type;
        }

        if (!RemovedTypeAliases.TryGetValue(type.FullName, out var alias))
        {
            return type;
        }

        var oldName = type.FullName;
        type.Namespace = alias.Namespace;
        type.Name = alias.Name;
        type.DeclaringType = null;
        if (type.Scope is not Cecil.AssemblyNameReference scope ||
            !scope.Name.Equals(alias.Assembly, StringComparison.OrdinalIgnoreCase))
        {
            type.Scope = ResolveAssemblyReference(module, alias.Assembly);
        }

        changes.Add($"{oldName} → {alias.Namespace}.{alias.Name}");
        return type;
    }

    /// <summary>
    /// 修正因成员重写而需要调整的 IL 指令（构造调用改静态调用、字段读取改方法调用等）。
    /// </summary>
    private static void FixInstructionOperands(Cecil.ModuleDefinition module, List<string> changes)
    {
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var instruction in method.Body.Instructions)
                {
                    switch (instruction.Operand)
                    {
                        case Cecil.MethodReference reference:
                        {
                            var targetMethod = reference is Cecil.GenericInstanceMethod genericMethod
                                ? genericMethod.ElementMethod
                                : reference;
                            RewriteMethod(module, targetMethod, changes);
                            NormalizeMethodSignature(module, targetMethod, changes);
                            if (reference is Cecil.GenericInstanceMethod genericReference)
                            {
                                for (var i = 0; i < genericReference.GenericArguments.Count; i++)
                                {
                                    genericReference.GenericArguments[i] =
                                        NormalizeType(module, genericReference.GenericArguments[i], changes);
                                }
                            }

                            if (instruction.OpCode == CecilIL.OpCodes.Newobj && !targetMethod.HasThis)
                            {
                                instruction.OpCode = CecilIL.OpCodes.Call;
                            }
                            else if (instruction.OpCode == CecilIL.OpCodes.Callvirt && !targetMethod.HasThis)
                            {
                                instruction.OpCode = CecilIL.OpCodes.Call;
                            }

                            break;
                        }
                        case Cecil.FieldReference field:
                            RewriteField(module, field, changes);
                            if (field.DeclaringType != null)
                            {
                                field.DeclaringType = NormalizeType(module, field.DeclaringType, changes);
                            }

                            field.FieldType = NormalizeType(module, field.FieldType, changes);
                            if (TryGetFieldReplacement(module, field, out var replacement))
                            {
                                instruction.OpCode = CecilIL.OpCodes.Call;
                                instruction.Operand = replacement;
                            }

                            break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// ToggleButton 的 Checked/Unchecked 事件在 Avalonia 12 中合并为 IsCheckedChanged，
    /// 这里把字段读取改成宿主垫片的方法调用。
    /// </summary>
    private static bool TryGetFieldReplacement(
        Cecil.ModuleDefinition module,
        Cecil.FieldReference field,
        out Cecil.MethodReference replacement)
    {
        replacement = null!;
        if (field.DeclaringType == null ||
            GetElementTypeFullName(field.DeclaringType) != "Avalonia.Controls.Primitives.ToggleButton")
        {
            return false;
        }

        var methodName = field.Name switch
        {
            "CheckedEvent" => "GetCheckedEvent",
            "UncheckedEvent" => "GetUncheckedEvent",
            _ => null
        };
        if (methodName == null)
        {
            return false;
        }

        var routedEvent = MakeGenericType(
            module,
            "Avalonia.Interactivity",
            "RoutedEvent`1",
            "Avalonia.Base",
            MakeTypeReference(module, "Avalonia.Interactivity", "RoutedEventArgs", "Avalonia.Base"));
        var method = new Cecil.MethodReference(methodName, routedEvent, ShimType(module))
        {
            HasThis = false
        };
        method.Parameters.Add(new Cecil.ParameterDefinition(
            MakeTypeReference(module, "Avalonia.Controls.Primitives", "ToggleButton", "Avalonia.Controls")));
        replacement = method;
        return true;
    }

    /// <summary>
    /// 取类型引用的定义名（泛型实例、数组等取最内层的定义类型）。
    /// </summary>
    private static string GetElementTypeFullName(Cecil.TypeReference type)
    {
        while (type is Cecil.TypeSpecification specification && specification.ElementType != null)
        {
            var element = specification.ElementType;
            if (ReferenceEquals(element, type))
            {
                break;
            }

            type = element;
        }

        return type.FullName;
    }

    /// <summary>
    /// 构造指向宿主程序集的类型引用。
    /// </summary>
    private static Cecil.TypeReference MakeTypeReference(
        Cecil.ModuleDefinition module,
        string ns,
        string name,
        string assemblyName) =>
        new(ns, name, module, ResolveAssemblyReference(module, assemblyName));

    /// <summary>
    /// 构造指向宿主程序集的泛型实例引用。
    /// </summary>
    private static Cecil.GenericInstanceType MakeGenericType(
        Cecil.ModuleDefinition module,
        string ns,
        string name,
        string assemblyName,
        params Cecil.TypeReference[] arguments)
    {
        var generic = new Cecil.GenericInstanceType(MakeTypeReference(module, ns, name, assemblyName));
        foreach (var argument in arguments)
        {
            generic.GenericArguments.Add(argument);
        }

        return generic;
    }

    private static Cecil.TypeReference ShimType(Cecil.ModuleDefinition module) =>
        MakeTypeReference(module, "ClassIsland.Core.Compatibility", "Avalonia12Compatibility", "ClassIsland.Core");

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
