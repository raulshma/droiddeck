# DroidDeck.PluginPack

Packages a built DroidDeck plugin folder as a `.ddplugin` archive and prints the
SHA-256 hash the catalog needs.

```text
droiddeck-plugin-pack <plugin-directory> [--output <directory>] [--include-symbols]
```

The plugin directory must contain `plugin.json` and the entry assembly it
declares. The tool drops assemblies the host already provides, rejects packages
over the host limits, writes entries in sorted order with fixed timestamps so
identical inputs produce identical archives, and prints the hash and entry list.

From a repository checkout:

```text
dotnet run --project tools/DroidDeck.PluginPack -- path/to/build/output
```

See [docs/plugin-sdk/packaging.md](../../docs/plugin-sdk/packaging.md) for the
full packaging and publishing guide.
