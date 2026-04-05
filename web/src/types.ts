// ── Plugin configuration (mirrors PluginConfiguration.cs) ─────────────────────
export interface PluginConfig {
  DisabledSeriesIds: string[];
  DisabledSeasonIds: string[];
  DisabledMovieIds: string[];
  EnabledSpecialsSeasonIds: string[];
}

// ── Jellyfin item shapes ───────────────────────────────────────────────────────
export interface BaseItem {
  Id: string;
  Name?: string | null;
  IndexNumber?: number | null;
  ParentId?: string | null;
  ImageTags?: Record<string, string> | null;
}

export interface LibraryView {
  Id: string;
  Name?: string | null;
  CollectionType?: string | null;
}

export interface ItemQueryResult {
  Items?: BaseItem[] | null;
  TotalRecordCount?: number | null;
}

// ── Jellyfin globals injected by the dashboard ─────────────────────────────────
// Only the stable methods present in all supported Jellyfin versions are declared
// here.  Internal helpers such as getCurrentUserId() and getItems() are not part
// of the public API surface in Jellyfin 10.9+ and must not be relied upon.
declare global {
  interface Window {
    ApiClient: {
      serverAddress(): string;
      accessToken(): string;
      getPluginConfiguration(id: string): Promise<PluginConfig>;
      updatePluginConfiguration(id: string, config: PluginConfig): Promise<unknown>;
    };
  }
}
