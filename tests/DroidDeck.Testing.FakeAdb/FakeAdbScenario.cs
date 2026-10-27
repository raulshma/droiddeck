namespace DroidDeck.Testing.FakeAdb;

internal static class FakeAdbScenario
{
    public static string[] InitialDeviceLines(FakeAdbOptions options) => options.Scenario switch
    {
        "offline" => [DeviceLine(options.Serial, "offline")],
        "unauthorized" => [DeviceLine(options.Serial, "unauthorized")],
        "empty" or "hotplug" => [],
        _ => [DeviceLine(options.Serial, "device")],
    };

    public static string DeviceLine(string serial, string state) =>
        $"{serial} {state} product:shiba model:Pixel_8 device:shiba transport_id:1";

    public static string MetadataOutput() =>
        """
        ---PROP---
        [ro.product.manufacturer]: [Google]
        [ro.product.model]: [Pixel 8]
        [ro.product.name]: [shiba]
        [ro.build.version.release]: [15]
        [ro.build.version.sdk]: [35]
        [ro.product.cpu.abi]: [arm64-v8a]
        [ro.product.cpu.abilist]: [arm64-v8a,armeabi-v7a]
        [ro.build.type]: [userdebug]
        [ro.debuggable]: [1]
        [ro.build.characteristics]: [nosdcard]
        [ro.kernel.qemu]: [0]
        ---WM---
        Physical size: 1080x2400
        Physical density: 420
        ---BAT---
        level: 87
        status: 2
        ---NET---
        2: wlan0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500
            inet 192.168.1.42/24 brd 192.168.1.255 scope global wlan0
        ---DF---
        Filesystem     1K-blocks     Used Available Use% Mounted on
        /dev/block/dm-0 116552960 48291840 68261120  42% /data
        """;

    public static string? PropertyValue(string name) => name switch
    {
        "ro.product.manufacturer" => "Google",
        "ro.product.model" => "Pixel 8",
        "ro.product.name" => "shiba",
        "ro.build.version.release" => "15",
        "ro.build.version.sdk" => "35",
        "ro.product.cpu.abi" => "arm64-v8a",
        "ro.product.cpu.abilist" => "arm64-v8a,armeabi-v7a",
        "ro.build.type" => "userdebug",
        "ro.debuggable" => "1",
        "ro.build.characteristics" => "nosdcard",
        "ro.kernel.qemu" => "0",
        _ => null,
    };
}
