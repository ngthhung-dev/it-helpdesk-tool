using System.IO;
using System.Text;
using ITHelpdeskTool.Reinstall;
using Xunit;

namespace ITHelpdeskTool.Reinstall.Tests;

public class LegacyTests
{
    private const string Xml =
        "<WIM><TOTALBYTES>123</TOTALBYTES>" +
        "<IMAGE INDEX=\"2\"><TOTALBYTES>2000</TOTALBYTES><WINDOWS><ARCH>9</ARCH><EDITIONID>Professional</EDITIONID>" +
        "<LANGUAGES><LANGUAGE>en-US</LANGUAGE><DEFAULT>en-US</DEFAULT></LANGUAGES>" +
        "<VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>19045</BUILD><SPBUILD>2006</SPBUILD></VERSION></WINDOWS>" +
        "<NAME>Windows 10 Pro</NAME><DESCRIPTION>Windows 10 Pro</DESCRIPTION></IMAGE>" +
        "<IMAGE INDEX=\"1\"><TOTALBYTES>1000</TOTALBYTES><WINDOWS><ARCH>0</ARCH><EDITIONID>Core</EDITIONID>" +
        "<LANGUAGES><LANGUAGE>vi-VN</LANGUAGE></LANGUAGES>" +
        "<VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>19045</BUILD></VERSION></WINDOWS>" +
        "<NAME>Windows 10 Home</NAME></IMAGE>" +
        "<IMAGE INDEX=\"3\"><NAME>Windows Setup Media</NAME></IMAGE></WIM>";

    /// <summary>File WIM giả: header 208 byte, rhXmlData ở byte 72 trỏ tới XML UTF-16 có BOM.</summary>
    private static byte[] FakeWim(string xml)
    {
        var body = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
        var head = new byte[208];
        Encoding.ASCII.GetBytes("MSWIM\0\0\0").CopyTo(head, 0);
        var offset = 208L + 50;   // có khoảng trống giữa header và XML
        for (var i = 0; i < 7; i++) head[72 + i] = (byte)(body.LongLength >> (8 * i));
        BitConverter.GetBytes(offset).CopyTo(head, 80);
        BitConverter.GetBytes(body.LongLength).CopyTo(head, 88);
        return head.Concat(new byte[50]).Concat(body).ToArray();
    }

    [Fact]
    public void Reads_editions_from_wim_xml()
    {
        var list = WimXml.Read(new MemoryStream(FakeWim(Xml)));
        Assert.Equal(new[] { 1, 2, 3 }, list.Select(e => e.Index));
        var pro = list[1];
        Assert.Equal("Windows 10 Pro", pro.Name);
        Assert.Equal("x64", pro.Architecture);
        Assert.Equal("Professional", pro.Edition);
        Assert.Equal("en-US", pro.Language);
        Assert.Equal("10.0.19045", pro.Version);
        Assert.Equal(19045, pro.Build);
        Assert.Equal(2000, pro.Size);
        Assert.Equal("x86", list[0].Architecture);
        Assert.Equal("vi-VN", list[0].Language);   // không có DEFAULT → lấy LANGUAGE
        Assert.True(MediaBuilder.IsSetupImage(list[2]));
        Assert.Equal("amd64", ImageInfo.UnattendArch(pro.Architecture));
    }

    [Fact]
    public void Rejects_non_wim_file()
    {
        var bytes = new byte[300];
        Assert.Throws<ReinstallException>(() => WimXml.Read(new MemoryStream(bytes)));
    }

    [Theory]
    [InlineData("0", "x86")]
    [InlineData("9", "x64")]
    [InlineData("12", "ARM64")]
    [InlineData(null, "")]
    public void Maps_arch_codes(string? code, string name) => Assert.Equal(name, WimXml.Arch(code));
}
