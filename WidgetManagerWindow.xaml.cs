using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace MailWidget;

public partial class WidgetManagerWindow : Window
{
    private readonly MainWindow _mainWindow;
    private bool _isDragging;

    public WidgetManagerWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
        Owner = mainWindow;
        InitializeComponent();
        Loaded += WidgetManagerWindow_Loaded;
    }

    private void WidgetManagerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        VersionText.Text = $"Version installée : {_mainWindow.CurrentVersionText}";
        InstallPathText.Text = $"Installation : {_mainWindow.InstallationDirectory}";
        ManagerStatusText.Text = "Prêt. Choisis une action ci-dessus.";
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        ManagerStatusText.Text = "Recherche d'une mise à jour…";

        try
        {
            var update = await _mainWindow.CheckForUpdateFromManagerAsync();
            if (update is null)
            {
                ManagerStatusText.Text = "Aucune mise à jour disponible.";
                return;
            }

            ManagerStatusText.Text = $"Version {update.VersionText} disponible. Clique sur le bouton d'installation.";
            InstallUpdateButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ManagerStatusText.Text = $"Recherche impossible : {ex.Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        InstallUpdateButton.IsEnabled = false;
        ManagerStatusText.Text = "La mise à jour démarre. Le widget va redémarrer…";
        _mainWindow.InstallAvailableUpdateFromManager();
    }

    private async void RefreshMailButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshMailButton.IsEnabled = false;
        ManagerStatusText.Text = "Actualisation de Gmail…";

        try
        {
            await _mainWindow.RefreshFromManagerAsync();
            ManagerStatusText.Text = "Gmail a été actualisé.";
        }
        catch (Exception ex)
        {
            ManagerStatusText.Text = $"Actualisation impossible : {ex.Message}";
        }
        finally
        {
            RefreshMailButton.IsEnabled = true;
        }
    }

    private void ShowWidgetButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.ShowWidgetFromManager();
        Close();
    }

    private async void RepairStartupButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _mainWindow.RepairStartupFromManagerAsync();
            ManagerStatusText.Text = "Le lancement automatique de Windows est réparé.";
        }
        catch (Exception ex)
        {
            ManagerStatusText.Text = $"Réparation impossible : {ex.Message}";
        }
    }

    private void OpenInstallFolderButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(_mainWindow.InstallationDirectory);
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(_mainWindow.SettingsDirectory);
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = string.Concat('"', path, '"'),
            UseShellExecute = true
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _isDragging = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The window may already be closing.
        }
        finally
        {
            _isDragging = false;
        }
    }
}
