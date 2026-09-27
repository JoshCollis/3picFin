using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Rowan.Jellyfin.Plugin.Configuration;

namespace Rowan.Jellyfin.Plugin;

/// <summary>Entry point for the Rowan plugin.</summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes the plugin without starting any external services.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Current = this;
    }

    /// <summary>Active plugin configuration; registration itself does not start upstream I/O.</summary>
    internal static Plugin? Current { get; private set; }

    /// <inheritdoc />
    public override string Name => "3pic Fin";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("bd36ab75-0f4a-49b6-92ef-3a93da040c7a");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
        }
    ];
}
