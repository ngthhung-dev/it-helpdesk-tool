using ITHelpdeskTool.Reinstall;
using Xunit;

namespace ITHelpdeskTool.Reinstall.Tests;

public class DownloadTests
{
    private const string Xml = """
        <MCT><Catalogs><Catalog version="1.4.1"><PublishedMedia><Files>
          <File id=""><FileName>26100.4349.250607-1500.ge_release_svc_refresh_CLIENTCONSUMER_RET_x64FRE_en-us.esd</FileName><LanguageCode>en-us</LanguageCode><Language>English (United States)</Language><Edition>Professional</Edition><Architecture>x64</Architecture><Size>4680320173</Size><Sha1>8CEAB2838F8E90180AC7490E8752157FB05588CB</Sha1><FilePath>http://dl.delivery.mp.microsoft.com/filestreamingservice/files/x/26100.4349.250607-1500.ge_release_svc_refresh_CLIENTCONSUMER_RET_x64FRE_en-us.esd</FilePath></File>
          <File id=""><FileName>26100.4349.250607-1500.ge_release_svc_refresh_CLIENTCONSUMER_RET_x64FRE_en-us.esd</FileName><LanguageCode>en-us</LanguageCode><Language>English (United States)</Language><Edition>Core</Edition><Architecture>x64</Architecture><Size>4680320173</Size><Sha1>8ceab2838f8e90180ac7490e8752157fb05588cb</Sha1><FilePath>http://dl.delivery.mp.microsoft.com/filestreamingservice/files/x/26100.4349.250607-1500.ge_release_svc_refresh_CLIENTCONSUMER_RET_x64FRE_en-us.esd</FilePath></File>
          <File id=""><FileName>26100.4349.250607-1500.ge_release_svc_refresh_CLIENTBUSINESS_VOL_x64FRE_en-us.esd</FileName><LanguageCode>en-us</LanguageCode><Language>English (United States)</Language><Edition>Enterprise</Edition><Architecture>x64</Architecture><Size>4612287490</Size><Sha1>e64ace7b54ecbb73c7c1bfad165a50e1776da30b</Sha1><FilePath>http://dl.delivery.mp.microsoft.com/filestreamingservice/files/y/26100.4349.250607-1500.ge_release_svc_refresh_CLIENTBUSINESS_VOL_x64FRE_en-us.esd</FilePath></File>
          <File id=""><FileName>evil.esd</FileName><LanguageCode>en-us</LanguageCode><Language>English</Language><Edition>Professional</Edition><Architecture>x64</Architecture><Size>1</Size><Sha1>00</Sha1><FilePath>http://example.com/evil.esd</FilePath></File>
        </Files></PublishedMedia></Catalog></Catalogs></MCT>
        """;

    [Fact]
    public void Parses_catalog_and_groups_editions()
    {
        var files = EsdCatalog.Parse(Xml, EsdCatalog.Windows11);
        Assert.Equal(2, files.Count);                       // link ngoài Microsoft bị bỏ
        var consumer = Assert.Single(files, f => f.Consumer);
        Assert.Equal(new[] { "Professional", "Core" }, consumer.Editions);
        Assert.Equal("26100.4349", consumer.Build);
        Assert.Equal("8ceab2838f8e90180ac7490e8752157fb05588cb", consumer.Sha1);
        Assert.Same(consumer, EsdCatalog.Pick(files, "EN-US", "x64"));
        Assert.Null(EsdCatalog.Pick(files, "vi-vn", "x64"));
    }

    [Theory]
    [InlineData("http://dl.delivery.mp.microsoft.com/a.esd", true)]
    [InlineData("https://download.microsoft.com/a.cab", true)]
    [InlineData("https://microsoft.com.evil.net/a.esd", false)]
    [InlineData("ftp://dl.delivery.mp.microsoft.com/a.esd", false)]
    public void Only_microsoft_hosts(string url, bool ok) => Assert.Equal(ok, EsdCatalog.IsMicrosoftUrl(url));

    [Fact]
    public void Setup_images_are_not_editions()
    {
        Assert.True(MediaBuilder.IsSetupImage(new ImageEdition { Name = "Windows Setup Media" }));
        Assert.True(MediaBuilder.IsSetupImage(new ImageEdition { Name = "Microsoft Windows PE (amd64)" }));
        Assert.True(MediaBuilder.IsSetupImage(new ImageEdition { Name = "Microsoft Windows Setup (amd64)" }));
        Assert.False(MediaBuilder.IsSetupImage(new ImageEdition { Name = "Windows 11 Pro" }));
    }

    [Fact]
    public void Sha1_of_file()
    {
        var f = Path.GetTempFileName();
        File.WriteAllText(f, "abc");
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", EsdDownloader.Sha1(f));
        File.Delete(f);
    }
}
