import type { BaseItem, ItemQueryResult, PluginConfig } from "./types.ts";

const PLUGIN_ID = "b2a63e62-0ac5-4575-9ad2-2c7534ccb83d";

export function loadConfig(): Promise<PluginConfig> {
  return window.ApiClient.getPluginConfiguration(PLUGIN_ID);
}

export function saveConfig(config: PluginConfig): Promise<unknown> {
  return window.ApiClient.updatePluginConfiguration(PLUGIN_ID, config);
}

export async function fetchSeries(): Promise<{ items: BaseItem[]; total: number }> {
  const userId = window.ApiClient.getCurrentUserId();
  const result: ItemQueryResult = await window.ApiClient.getItems(userId, {
    IncludeItemTypes: "Series",
    Recursive: true,
    Fields: "ImageTags",
    SortBy: "SortName",
    SortOrder: "Ascending",
  });
  return {
    items: result.Items ?? [],
    total: result.TotalRecordCount ?? 0,
  };
}

export async function fetchSeasons(seriesId: string): Promise<BaseItem[]> {
  const userId = window.ApiClient.getCurrentUserId();
  const result: ItemQueryResult = await window.ApiClient.getItems(userId, {
    ParentId: seriesId,
    IncludeItemTypes: "Season",
    Fields: "ImageTags",
    SortBy: "IndexNumber",
    SortOrder: "Ascending",
  });
  return result.Items ?? [];
}

/**
 * Returns a Jellyfin image URL for the given item ID.
 * Matches the approach used by intro-skipper-dashboard — no auth token needed
 * for images; fillHeight lets Jellyfin resize server-side.
 */
export function getImageUrl(itemId: string, fillHeight = 136): string {
  const base = window.ApiClient.serverAddress().replace(/\/+$/, "");
  return `${base}/Items/${encodeURIComponent(itemId)}/Images/Primary?fillHeight=${fillHeight}&quality=90`;
}
