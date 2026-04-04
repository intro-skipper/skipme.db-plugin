// ── Plugin configuration (mirrors PluginConfiguration.cs) ─────────────────────
export interface PluginConfig {
  DisabledSeriesIds: string[];
  DisabledSeasonIds: string[];
}

// ── Jellyfin item shapes ───────────────────────────────────────────────────────
export interface BaseItem {
  Id: string;
  Name?: string | null;
  IndexNumber?: number | null;
  ImageTags?: Record<string, string> | null;
}

export interface ItemQueryResult {
  Items?: BaseItem[] | null;
  TotalRecordCount?: number | null;
}

// ── Jellyfin globals injected by the dashboard ─────────────────────────────────
declare global {
  interface Window {
    ApiClient: {
      serverAddress(): string;
      accessToken(): string;
      getCurrentUserId(): string;
      getPluginConfiguration(id: string): Promise<PluginConfig>;
      updatePluginConfiguration(id: string, config: PluginConfig): Promise<unknown>;
      getItems(
        userId: string,
        params: Record<string, string | boolean | number | undefined>,
      ): Promise<ItemQueryResult>;
    };
  }
}
