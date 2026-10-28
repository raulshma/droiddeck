using DroidDeck.Projects.Artifacts;

namespace DroidDeck.Projects.Tests.Resolution;

public sealed class Aapt2ApkInspectorTests
{
    [Fact]
    public void Parse_ExtractsPackageSdkLaunchableAbisAndPermissions()
    {
        const string output = """
            package: name='com.example.app' versionCode='42' versionName='1.2.3'
            sdkVersion:'24'
            targetSdkVersion:'35'
            uses-permission: name='android.permission.INTERNET'
            uses-permission: name='android.permission.CAMERA'
            application-label:'Sample'
            application-label-en:'Sample'
            launchable-activity: name='com.example.app.MainActivity'  label='Sample' icon=''
            native-code: 'arm64-v8a' 'x86_64'
            """;

        var info = Aapt2ApkInspector.Parse(@"C:\repo\app.apk", output);

        Assert.NotNull(info);
        Assert.Equal(@"C:\repo\app.apk", info.Path);
        Assert.Equal("com.example.app", info.PackageName);
        Assert.Equal(42, info.VersionCode);
        Assert.Equal("1.2.3", info.VersionName);
        Assert.Equal(24, info.MinSdk);
        Assert.Equal(35, info.TargetSdk);
        Assert.Equal("com.example.app.MainActivity", info.LaunchableActivity);
        Assert.Equal("Sample", info.ApplicationLabel);
        Assert.Equal(["arm64-v8a", "x86_64"], info.Abis);
        Assert.Equal(
            ["android.permission.INTERNET", "android.permission.CAMERA"],
            info.Permissions);
        Assert.False(info.IsSplit);
    }

    [Fact]
    public void Parse_RecognizesSplitApk()
    {
        const string output = """
            package: name='com.example.app' versionCode='42' versionName='1.2.3' split='config.arm64'
            sdkVersion:'24'
            """;

        var info = Aapt2ApkInspector.Parse("split.apk", output);

        Assert.NotNull(info);
        Assert.True(info.IsSplit);
        Assert.Equal("config.arm64", info.SplitName);
    }

    [Fact]
    public void Parse_ReturnsNullWithoutPackageName()
    {
        Assert.Null(Aapt2ApkInspector.Parse("bad.apk", "not an apk"));
    }
}
