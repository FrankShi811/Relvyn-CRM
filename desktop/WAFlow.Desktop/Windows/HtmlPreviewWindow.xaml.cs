using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace WAFlow.Desktop.Windows;

/// <summary>Renders an email's original HTML body (tables, buttons, links) with WebView2.
/// External http(s) links open in the system browser instead of navigating the preview.
/// Local inline images (cid:) are served through a virtual host mapping so
/// NavigateToString pages can load them (file:// is blocked by WebView2).</summary>
public partial class HtmlPreviewWindow : Window
{
    private readonly string _html;
    private readonly string _attachmentRoot;

    public HtmlPreviewWindow(string subject, string html, string attachmentRoot)
    {
        InitializeComponent();
        _html = html;
        _attachmentRoot = attachmentRoot;
        Title = string.IsNullOrWhiteSpace(subject) ? "原邮件" : $"原邮件 · {subject}";
        PreviewWeb.CoreWebView2InitializationCompleted += OnCoreWebView2Initialized;
        Loaded += async (_, _) =>
        {
            try
            {
                await PreviewWeb.EnsureCoreWebView2Async();
                PreviewWeb.NavigateToString(_html);
            }
            catch (Exception error)
            {
                MessageBox.Show($"无法打开原邮件：{error.Message}", "原邮件", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
    }

    private void OnCoreWebView2Initialized(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        PreviewWeb.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        PreviewWeb.CoreWebView2.Settings.IsZoomControlEnabled = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(_attachmentRoot) && Directory.Exists(_attachmentRoot))
            {
                PreviewWeb.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "email-attachments",
                    _attachmentRoot,
                    CoreWebView2HostResourceAccessKind.Allow);
            }
        }
        catch
        {
            // Without the mapping, cid images simply render as broken images.
        }
        PreviewWeb.CoreWebView2.NavigationStarting += OnNavigationStarting;
        PreviewWeb.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            OpenExternal(e.Uri);
            e.Cancel = true;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        OpenExternal(e.Uri);
        e.Handled = true;
    }

    private static void OpenExternal(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // Opening the system browser is best-effort.
        }
    }
}
