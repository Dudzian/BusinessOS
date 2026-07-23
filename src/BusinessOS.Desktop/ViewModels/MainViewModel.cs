using BusinessOS.AppHost;

namespace BusinessOS.Desktop.ViewModels;

public sealed class MainViewModel(ProductInfo productInfo)
{
    public string Title => productInfo.Name;

    public string FoundationHeading => "Foundation";

    public string StatusMessage => "Fundament aplikacji został uruchomiony";

    public string Description => "Minimalny shell desktopowy gotowy na kolejne bloki BusinessOS.";

    public string Version => $"Version {productInfo.Version}";
}
