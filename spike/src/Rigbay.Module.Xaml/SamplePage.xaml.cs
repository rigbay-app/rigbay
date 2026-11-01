using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Rigbay.Module.Xaml;

public sealed partial class SamplePage : Page
{
    public SamplePage()
    {
        // Default generated path: ms-appx:///Rigbay.Module.Xaml/SamplePage.xaml, i.e. <host exe dir>\Rigbay.Module.Xaml\SamplePage.xbf.
        // An unpackaged host resolves ms-appx against its own exe folder, not against the module folder.
        // Variant B keeps everything in the module folder: the host exe dir is the ms-appx root, so a relative
        // path "modules/<module>/<assembly>/SamplePage.xaml" points at the xbf shipped inside the module.
        var how = "default InitializeComponent";
        try { InitializeComponent(); }
        catch (Exception)
        {
            var moduleDir = Path.GetDirectoryName(typeof(SamplePage).Assembly.Location)!;
            var rel = Path.GetRelativePath(AppContext.BaseDirectory, moduleDir).Replace('\\', '/');
            Application.LoadComponent(this, new Uri($"ms-appx:///{rel}/Rigbay.Module.Xaml/SamplePage.xaml"), ComponentResourceLocation.Nested);
            how = $"module-relative ms-appx:///{rel}/...";
        }
        Info.Text = "Compiled XAML, loaded through " + how + ".";
    }
}
