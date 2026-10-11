using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DriverForge.ViewModels;

/// <summary>Where drivers come from (SPEC §4): the Catalog switch and the user's driver folders.</summary>
public sealed partial class SourcesViewModel : ObservableObject
{
    readonly MainViewModel main;

    public SourcesViewModel(MainViewModel main)
    {
        this.main = main;
        Folders = new(main.Settings.DriverFolders);
    }

    public ObservableCollection<string> Folders { get; }

    public bool UseCatalog
    {
        get => main.Settings.UseCatalog;
        set { main.Settings.UseCatalog = value; main.SaveSettings(); OnPropertyChanged(); }
    }

    [RelayCommand]
    void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder of drivers" };
        if (dialog.ShowDialog() != true) return;
        if (main.Settings.DriverFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) return;
        main.Settings.DriverFolders.Add(dialog.FolderName);
        main.SaveSettings();
        Folders.Add(dialog.FolderName);
    }

    [RelayCommand]
    void RemoveFolder(string folder)
    {
        main.Settings.DriverFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        main.SaveSettings();
        Folders.Remove(folder);
    }

    [RelayCommand]
    async Task Rescan()
    {
        main.ShowDrivers();
        await main.Drivers.ScanAsync();
    }
}
