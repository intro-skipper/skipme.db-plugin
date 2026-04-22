import type { BaseItem, ItemQueryResult, LibraryView, PluginConfig, VirtualFolderInfo } from "./types.ts";

const PLUGIN_ID = "b2a63e62-0ac5-4575-9ad2-2c7534ccb83d";

// ── Auth helper ────────────────────────────────────────────────────────────────
// Uses only the two stable window.ApiClient methods: serverAddress() and
// accessToken(). All library data is fetched via the Jellyfin REST API directly
// rather than through the internal ApiClient wrappers (getCurrentUserId /
// getItems) which are not part of the stable public surface in Jellyfin 10.9+.
async function fetchWithAuth(path: string): Promise<Response> {
  const base = window.ApiClient.serverAddress().replace(/\/+$/, "");
  const token = window.ApiClient.accessToken();
  return fetch(`${base}/${path}`, {
    headers: { Authorization: `MediaBrowser Token=${token}` },
  });
}

// ── Current user (cached for the lifetime of the page) ────────────────────────
let _cachedUserId: string | null = null;

async function getCurrentUserId(): Promise<string> {
  if (_cachedUserId) return _cachedUserId;
  const res = await fetchWithAuth("Users/Me");
  if (!res.ok) throw new Error(`Failed to get current user (HTTP ${res.status})`);
  const data = (await res.json()) as { Id: string };
  _cachedUserId = data.Id;
  return data.Id;
}

// ── Plugin config ──────────────────────────────────────────────────────────────
export function loadConfig(): Promise<PluginConfig> {
  return window.ApiClient.getPluginConfiguration(PLUGIN_ID);
}

export function saveConfig(config: PluginConfig): Promise<unknown> {
  return window.ApiClient.updatePluginConfiguration(PLUGIN_ID, config);
}

// ── Library items ──────────────────────────────────────────────────────────────
export async function fetchSeries(): Promise<{ items: BaseItem[]; total: number }> {
  const userId = await getCurrentUserId();
  const params = new URLSearchParams({
    IncludeItemTypes: "Series",
    Recursive: "true",
    Fields: "ImageTags",
    SortBy: "SortName",
    SortOrder: "Ascending",
    UserId: userId,
  });
  const res = await fetchWithAuth(`Items?${params.toString()}`);
  if (!res.ok) throw new Error(`Failed to fetch series (HTTP ${res.status})`);
  const data = (await res.json()) as ItemQueryResult;
  return { items: data.Items ?? [], total: data.TotalRecordCount ?? 0 };
}

export async function fetchSeriesForLibrary(libraryId: string): Promise<{ items: BaseItem[]; total: number }> {
  const userId = await getCurrentUserId();
  const params = new URLSearchParams({
    ParentId: libraryId,
    IncludeItemTypes: "Series",
    Recursive: "true",
    Fields: "ImageTags",
    SortBy: "SortName",
    SortOrder: "Ascending",
    UserId: userId,
  });
  const res = await fetchWithAuth(`Items?${params.toString()}`);
  if (!res.ok) throw new Error(`Failed to fetch series for library (HTTP ${res.status})`);
  const data = (await res.json()) as ItemQueryResult;
  return { items: data.Items ?? [], total: data.TotalRecordCount ?? 0 };
}

export async function fetchSeasons(seriesId: string): Promise<BaseItem[]> {
  const params = new URLSearchParams({
    ParentId: seriesId,
    IncludeItemTypes: "Season",
    Fields: "ImageTags",
    SortBy: "IndexNumber",
    SortOrder: "Ascending",
  });
  const res = await fetchWithAuth(`Items?${params.toString()}`);
  if (!res.ok) throw new Error(`Failed to fetch seasons (HTTP ${res.status})`);
  const data = (await res.json()) as ItemQueryResult;
  return data.Items ?? [];
}

export async function fetchMovies(): Promise<{ items: BaseItem[]; total: number }> {
  const userId = await getCurrentUserId();
  const params = new URLSearchParams({
    IncludeItemTypes: "Movie",
    Recursive: "true",
    Fields: "ImageTags",
    SortBy: "SortName",
    SortOrder: "Ascending",
    UserId: userId,
  });
  const res = await fetchWithAuth(`Items?${params.toString()}`);
  if (!res.ok) throw new Error(`Failed to fetch movies (HTTP ${res.status})`);
  const data = (await res.json()) as ItemQueryResult;
  return { items: data.Items ?? [], total: data.TotalRecordCount ?? 0 };
}

export async function fetchMoviesForLibrary(libraryId: string): Promise<{ items: BaseItem[]; total: number }> {
  const userId = await getCurrentUserId();
  const params = new URLSearchParams({
    ParentId: libraryId,
    IncludeItemTypes: "Movie",
    Recursive: "true",
    Fields: "ImageTags",
    SortBy: "SortName",
    SortOrder: "Ascending",
    UserId: userId,
  });
  const res = await fetchWithAuth(`Items?${params.toString()}`);
  if (!res.ok) throw new Error(`Failed to fetch movies for library (HTTP ${res.status})`);
  const data = (await res.json()) as ItemQueryResult;
  return { items: data.Items ?? [], total: data.TotalRecordCount ?? 0 };
}

export async function fetchLibraries(): Promise<LibraryView[]> {
  const userId = await getCurrentUserId();
  const res = await fetchWithAuth(`Users/${encodeURIComponent(userId)}/Views`);
  if (!res.ok) throw new Error(`Failed to fetch libraries (HTTP ${res.status})`);
  const data = (await res.json()) as { Items?: LibraryView[] | null };
  return data.Items ?? [];
}

export async function fetchVirtualFolders(): Promise<VirtualFolderInfo[]> {
  const res = await fetchWithAuth("Library/VirtualFolders");
  if (!res.ok) throw new Error(`Failed to fetch virtual folders (HTTP ${res.status})`);
  const data = (await res.json()) as VirtualFolderInfo[] | null;
  return data ?? [];
}

// ── Image URLs ─────────────────────────────────────────────────────────────────
/**
 * Returns a Jellyfin image URL for the given item ID.
 * Uses fillHeight so Jellyfin resizes server-side; no auth token is required
 * for image endpoints.
 */
export function getImageUrl(itemId: string, fillHeight = 136): string {
  const base = window.ApiClient.serverAddress().replace(/\/+$/, "");
  return `${base}/Items/${encodeURIComponent(itemId)}/Images/Primary?fillHeight=${fillHeight}&quality=90`;
}
