// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using SkipMe.Db.Plugin.Configuration;

namespace SkipMe.Db.Plugin;

/// <summary>
/// The SkipMe.db plugin for Jellyfin.
/// Retrieves crowd-sourced intro/credits/recap/preview segment timestamps
/// from db.skipme.workers.dev and exposes them via the Jellyfin media segments API.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILibraryManager libraryManager)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        LibraryManager = libraryManager;
    }

    /// <inheritdoc/>
    public override string Name => "SkipMe.db";

    /// <inheritdoc/>
    public override Guid Id => Guid.Parse("b2a63e62-0ac5-4575-9ad2-2c7534ccb83d");

    /// <inheritdoc/>
    public override string Description => "Retrieves crowd-sourced segment timestamps from the SkipMe.db API";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the library manager used to resolve items by ID.
    /// </summary>
    public ILibraryManager LibraryManager { get; }

    /// <inheritdoc/>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            },
        ];
    }
}
