using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using ClassIsland.Controls;
using ClassIsland.Core;
using ClassIsland.Core.Helpers.UI;
using ClassIsland.Core.Models.UI;
using ClassIsland.ViewModels;
using FluentAvalonia.UI.Controls;

namespace ClassIsland.Views.WelcomePages;

public partial class WelcomePage : UserControl, IWelcomePage
{
    public WelcomeViewModel ViewModel { get; set; } = null!;
    
    public WelcomePage()
    {
        InitializeComponent();
    }

    private void ButtonNext_OnClick(object? sender, RoutedEventArgs e)
    {
        NavigateNext();
    }

    private void NavigateNext()
    {
        WelcomeWindow.WelcomeNavigateForwardCommand.Execute(this);
    }

    private void Intro_OnAnimationEnd(object? sender, EventArgs e)
    {
        ContentRoot.Classes.Add("anim");
    }

    private void ButtonDataMigration_OnClick(object? sender, RoutedEventArgs e)
    {
        var welcomeWindow = this.FindAncestorOfType<WelcomeWindow>();
        if (welcomeWindow == null)
        {
            return;
        }

        welcomeWindow.Pages.Clear();
        welcomeWindow.Pages.AddRange([typeof(WelcomePage), typeof(LicensePage), typeof(DataTransferPage)]);
        
        NavigateNext();
    }

    private void ButtonEnterRecovery_OnClick(object? sender, RoutedEventArgs e)
    {
        AppBase.Current.Restart(["-m", "-r"]);
    }

    private void ButtonJoinManagement_OnClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new JoinManagementDialog();
        dialog.ShowDialog((TopLevel.GetTopLevel(this) as Window)!);
    }
}