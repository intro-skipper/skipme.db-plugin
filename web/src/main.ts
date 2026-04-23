import "./styles/main.css";

import type { BaseItem, LibraryView, ShareSubmitRequest, VirtualFolderInfo } from "./types.ts";
import {
  fetchLibraries,
  fetchMoviesForLibrary,
  fetchSeriesForLibrary,
  fetchSeasons,
  fetchVirtualFolders,
  getImageUrl,
  loadConfig,
  saveConfig,
  shareEnabledItems,
} from "./api.ts";

// ── Constants ──────────────────────────────────────────────────────────────────
const ROOT_SELECTOR = "#skipme-root";
const OBSERVER_TIMEOUT_MS = 30_000;
const SKIPME_PROVIDER_NAME = "skipme.db";
const SKIPME_PROVIDER_ID = "4dbabcc18d37fdc81c1dd513a47b70cb";
const SYNC_DESCRIPTION =
  "Toggle crowd-sourced segment data on or off for individual libraries, series, seasons, or movies. Segments remain in the local database but will not be surfaced to Jellyfin when disabled.";
const SHARE_DESCRIPTION =
  "Toggle local segment data on or off for individual libraries, series, seasons, or movies that will be shared with SkipMe.db. Each unique segment type can only be shared once per episode.";

// ── Library section data ───────────────────────────────────────────────────────
interface UnifiedSection {
  libraryId: string;
  libraryName: string;
  collectionType: string | null;
  seriesItems: BaseItem[];
  movieItems: BaseItem[];
  seriesTotalCount: number;
  moviesTotalCount: number;
}

// ── Page-level mutable state (reset on every mount) ───────────────────────────
let disabledSeriesIds = new Set<string>();
let disabledSeasonIds = new Set<string>();
let disabledMovieIds = new Set<string>();
let enabledSpecialsSeasonIds = new Set<string>();
let unifiedSections: UnifiedSection[] = [];
let filterQuery = "";
const seasonCache = new Map<string, BaseItem[]>();
let searchDebounce = 0;
let initRunning = false;
let eventsWired = false;
let activeTab: "sync" | "share" = "sync";
let filteredSeriesIds = new Set<string>();
let filteredMovieIds = new Set<string>();

// ── Share-tab independent state (always defaults to all disabled on page load) ──
let shareDisabledSeriesIds = new Set<string>();
let shareDisabledSeasonIds = new Set<string>();
let shareDisabledMovieIds = new Set<string>();
let shareEnabledSpecialsSeasonIds = new Set<string>();

// ── DOM helpers ────────────────────────────────────────────────────────────────
function byId<T extends HTMLElement = HTMLElement>(id: string): T | null {
  return document.getElementById(id) as T | null;
}

function show(id: string): void {
  const e = byId(id);
  if (e) e.style.display = "";
}

function hide(id: string): void {
  const e = byId(id);
  if (e) e.style.display = "none";
}

function setStatus(msg: string, type: "ok" | "err" | ""): void {
  const s = byId("skipme-status");
  if (!s) return;
  s.textContent = msg;
  s.className = "skipme-status" + (type ? " " + type : "");
  if (type === "ok") {
    window.setTimeout(() => {
      s.textContent = "";
      s.className = "skipme-status";
    }, 3000);
  }
}

function updateTopDescription(): void {
  const descriptionEl = byId("skipme-description");
  if (!descriptionEl) return;
  descriptionEl.textContent = activeTab === "share" ? SHARE_DESCRIPTION : SYNC_DESCRIPTION;
}

function setActiveTab(tab: "sync" | "share"): void {
  activeTab = tab;

  const syncTabBtn = byId<HTMLButtonElement>("skipme-tab-sync");
  const shareTabBtn = byId<HTMLButtonElement>("skipme-tab-share");
  const saveBtn = byId<HTMLButtonElement>("skipme-save-btn");
  const shareBtn = byId<HTMLButtonElement>("skipme-share-btn");

  const syncActive = tab === "sync";
  const shareActive = tab === "share";
  syncTabBtn?.classList.toggle("is-active", syncActive);
  shareTabBtn?.classList.toggle("is-active", shareActive);
  syncTabBtn?.setAttribute("aria-selected", syncActive ? "true" : "false");
  shareTabBtn?.setAttribute("aria-selected", shareActive ? "true" : "false");

  if (saveBtn) saveBtn.style.display = syncActive ? "" : "none";
  if (shareBtn) shareBtn.style.display = syncActive ? "none" : "";

  updateTopDescription();
  renderLibrarySections();
}

// ── Toggle switch component ────────────────────────────────────────────────────
interface Toggle {
  element: HTMLLabelElement;
  input: HTMLInputElement;
}

function createToggle(
  id: string,
  checked: boolean,
  onChange: (enabled: boolean) => void,
  title: string,
): Toggle {
  const label = document.createElement("label");
  label.className = "skipme-toggle";
  label.title = title;

  const input = document.createElement("input");
  input.type = "checkbox";
  input.id = id;
  input.checked = checked;

  const track = document.createElement("span");
  track.className = "skipme-toggle-track";

  const thumb = document.createElement("span");
  thumb.className = "skipme-toggle-thumb";
  track.appendChild(thumb);

  label.appendChild(input);
  label.appendChild(track);

  input.addEventListener("change", () => {
    label.title = input.checked ? "Enabled – click to disable" : "Disabled – click to enable";
    onChange(input.checked);
  });

  // Prevent clicks bubbling to the series header expand/collapse handler.
  label.addEventListener("click", (e) => e.stopPropagation());

  return { element: label, input };
}

// ── SVG chevron ────────────────────────────────────────────────────────────────
function createChevron(): SVGSVGElement {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "2.5");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  svg.setAttribute("class", "skipme-chevron");
  svg.setAttribute("aria-hidden", "true");
  const poly = document.createElementNS(ns, "polyline");
  poly.setAttribute("points", "6 9 12 15 18 9");
  svg.appendChild(poly);
  return svg;
}

// ── Tab-aware state accessors ──────────────────────────────────────────────────
// Cards are always created for the currently active tab, and tabs re-render on
// switch, so activeTab is stable for the entire lifetime of any given card.
function activeDisabledSeriesIds(): Set<string> {
  return activeTab === "sync" ? disabledSeriesIds : shareDisabledSeriesIds;
}
function activeDisabledSeasonIds(): Set<string> {
  return activeTab === "sync" ? disabledSeasonIds : shareDisabledSeasonIds;
}
function activeDisabledMovieIds(): Set<string> {
  return activeTab === "sync" ? disabledMovieIds : shareDisabledMovieIds;
}
function activeEnabledSpecialsSeasonIds(): Set<string> {
  return activeTab === "sync" ? enabledSpecialsSeasonIds : shareEnabledSpecialsSeasonIds;
}

// ── Season card ────────────────────────────────────────────────────────────────
function createSeasonCard(season: BaseItem, seriesId: string): HTMLElement {
  const seriesDisabled = activeDisabledSeriesIds().has(seriesId);
  const isSpecials = season.IndexNumber === 0;
  const seasonDisabled = isSpecials
    ? !activeEnabledSpecialsSeasonIds().has(season.Id)
    : activeDisabledSeasonIds().has(season.Id);

  const card = document.createElement("div");
  card.className = "skipme-season-card";
  if (seasonDisabled) card.style.opacity = "0.6";

  // Poster
  const posterWrap = document.createElement("div");
  posterWrap.className = "skipme-season-poster-wrap";

  if (season.ImageTags?.["Primary"]) {
    const img = document.createElement("img");
    img.className = "skipme-season-poster";
    img.alt = "";
    img.loading = "lazy";
    img.decoding = "async";
    img.src = getImageUrl(season.Id, 200);
    img.onerror = () => {
      img.style.display = "none";
      const ph = document.createElement("div");
      ph.className = "skipme-season-no-image";
      ph.textContent = "📺";
      posterWrap.insertBefore(ph, posterWrap.firstChild);
    };
    posterWrap.appendChild(img);
  } else {
    const ph = document.createElement("div");
    ph.className = "skipme-season-no-image";
    ph.textContent = "📺";
    posterWrap.appendChild(ph);
  }

  if (seriesDisabled) {
    const overlay = document.createElement("div");
    overlay.className = "skipme-season-overlay";
    overlay.textContent = "Disabled via series";
    posterWrap.appendChild(overlay);
  }

  card.appendChild(posterWrap);

  // Footer: name + toggle
  const footer = document.createElement("div");
  footer.className = "skipme-season-footer";

  const nameEl = document.createElement("div");
  nameEl.className = "skipme-season-name";
  const displayName = season.Name ?? "Season " + (season.IndexNumber ?? "?");
  nameEl.textContent = displayName;
  nameEl.title = displayName;

  const tog = createToggle(
    "skipme-season-" + season.Id,
    !seasonDisabled,
    (enabled) => {
      if (isSpecials) {
        if (enabled) {
          activeEnabledSpecialsSeasonIds().add(season.Id);
        } else {
          activeEnabledSpecialsSeasonIds().delete(season.Id);
        }
      } else {
        if (enabled) {
          activeDisabledSeasonIds().delete(season.Id);
        } else {
          activeDisabledSeasonIds().add(season.Id);
        }
      }
      card.style.opacity = enabled ? "" : "0.6";
    },
    seasonDisabled ? "Season disabled – click to enable" : "Season enabled – click to disable",
  );

  if (seriesDisabled) {
    tog.input.disabled = true;
    tog.element.title = "Enable the series to manage seasons individually";
  }

  footer.appendChild(nameEl);
  footer.appendChild(tog.element);
  card.appendChild(footer);

  return card;
}

// ── Seasons grid ───────────────────────────────────────────────────────────────
function renderSeasonsGrid(panel: HTMLElement, seasons: BaseItem[], seriesId: string): void {
  panel.innerHTML = "";

  if (!seasons.length) {
    const empty = document.createElement("p");
    empty.style.cssText = "opacity:0.45;font-size:0.85em;margin:4px 0";
    empty.textContent = "No seasons found.";
    panel.appendChild(empty);
    return;
  }

  // Sort so specials (IndexNumber === 0) appear last.
  const sorted = [...seasons].sort((a, b) => {
    const aIsSpecials = a.IndexNumber === 0;
    const bIsSpecials = b.IndexNumber === 0;
    if (aIsSpecials !== bIsSpecials) return aIsSpecials ? 1 : -1;
    return (a.IndexNumber ?? 0) - (b.IndexNumber ?? 0);
  });

  const grid = document.createElement("div");
  grid.className = "skipme-seasons-grid";
  for (const season of sorted) {
    grid.appendChild(createSeasonCard(season, seriesId));
  }
  panel.appendChild(grid);
}

function loadAndRenderSeasons(panel: HTMLElement, seriesId: string): void {
  const cached = seasonCache.get(seriesId);
  if (cached) {
    renderSeasonsGrid(panel, cached, seriesId);
    return;
  }

  panel.innerHTML = "";
  const loadingEl = document.createElement("div");
  loadingEl.className = "skipme-seasons-loading";
  loadingEl.innerHTML = '<div class="skipme-spinner"></div><span>Loading seasons…</span>';
  panel.appendChild(loadingEl);

  fetchSeasons(seriesId)
    .then((seasons) => {
      seasonCache.set(seriesId, seasons);
      renderSeasonsGrid(panel, seasons, seriesId);
    })
    .catch(() => {
      panel.innerHTML = '<p style="opacity:0.5;font-size:0.85em">Failed to load seasons.</p>';
    });
}

// ── Series card ────────────────────────────────────────────────────────────────
function createSeriesCard(series: BaseItem): HTMLElement {
  const isDisabled = activeDisabledSeriesIds().has(series.Id);

  const card = document.createElement("div");
  card.className = "skipme-series-card";

  // Header
  const header = document.createElement("div");
  header.className = "skipme-series-header";
  header.setAttribute("role", "button");
  header.setAttribute("tabindex", "0");
  header.setAttribute("aria-expanded", "false");

  // Poster
  let posterEl: HTMLElement;
  if (series.ImageTags?.["Primary"]) {
    const img = document.createElement("img");
    img.className = "skipme-series-poster";
    img.alt = "";
    img.loading = "lazy";
    img.decoding = "async";
    img.src = getImageUrl(series.Id, 96);
    img.onerror = () => {
      img.style.visibility = "hidden";
    };
    posterEl = img;
  } else {
    posterEl = document.createElement("div");
    posterEl.className = "skipme-series-poster-placeholder";
    posterEl.setAttribute("aria-hidden", "true");
    posterEl.textContent = "📺";
  }

  // Info
  const info = document.createElement("div");
  info.className = "skipme-series-info";

  const nameEl = document.createElement("div");
  nameEl.className = "skipme-series-name";
  nameEl.textContent = series.Name ?? "Unknown Series";

  const hint = document.createElement("div");
  hint.className = "skipme-series-hint" + (isDisabled ? " is-off" : "");
  hint.textContent = isDisabled
    ? "Segments disabled for all episodes"
    : "Expand to manage individual seasons";

  info.appendChild(nameEl);
  info.appendChild(hint);

  // Controls
  const controls = document.createElement("div");
  controls.className = "skipme-series-controls";

  const chevron = createChevron();

  // Seasons panel (created before toggle so the toggle's onChange can reference it)
  const seasonsPanel = document.createElement("div");
  seasonsPanel.className = "skipme-seasons-panel";

  const tog = createToggle(
    "skipme-series-" + series.Id,
    !isDisabled,
    (enabled) => {
      if (enabled) {
        activeDisabledSeriesIds().delete(series.Id);
        hint.textContent = "Expand to manage individual seasons";
        hint.className = "skipme-series-hint";
      } else {
        activeDisabledSeriesIds().add(series.Id);
        hint.textContent = "Segments disabled for all episodes";
        hint.className = "skipme-series-hint is-off";
      }

      // Re-render seasons panel if open so overlays update immediately.
      if (card.classList.contains("is-expanded")) {
        const cached = seasonCache.get(series.Id);
        if (cached) {
          renderSeasonsGrid(seasonsPanel, cached, series.Id);
        }
      }
    },
    isDisabled ? "Series disabled – click to enable" : "Series enabled – click to disable",
  );

  controls.appendChild(chevron);
  controls.appendChild(tog.element);

  header.appendChild(posterEl);
  header.appendChild(info);
  header.appendChild(controls);

  // Expand / collapse on click or keyboard
  const toggleExpand = (): void => {
    const expanded = card.classList.toggle("is-expanded");
    header.setAttribute("aria-expanded", expanded ? "true" : "false");

    if (expanded && !seasonsPanel.dataset["loaded"]) {
      seasonsPanel.dataset["loaded"] = "true";
      loadAndRenderSeasons(seasonsPanel, series.Id);
    }
  };

  header.addEventListener("click", toggleExpand);
  header.addEventListener("keydown", (e) => {
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      toggleExpand();
    }
  });

  card.appendChild(header);
  card.appendChild(seasonsPanel);

  return card;
}

// ── Movie card ─────────────────────────────────────────────────────────────────
function createMovieCard(movie: BaseItem): HTMLElement {
  const isDisabled = activeDisabledMovieIds().has(movie.Id);

  const card = document.createElement("div");
  card.className = "skipme-movie-card";
  if (isDisabled) card.style.opacity = "0.6";

  // Poster
  const posterWrap = document.createElement("div");
  posterWrap.className = "skipme-movie-poster-wrap";

  if (movie.ImageTags?.["Primary"]) {
    const img = document.createElement("img");
    img.className = "skipme-movie-poster";
    img.alt = "";
    img.loading = "lazy";
    img.decoding = "async";
    img.src = getImageUrl(movie.Id, 200);
    img.onerror = () => {
      img.style.display = "none";
      const ph = document.createElement("div");
      ph.className = "skipme-movie-no-image";
      ph.textContent = "🎬";
      posterWrap.insertBefore(ph, posterWrap.firstChild);
    };
    posterWrap.appendChild(img);
  } else {
    const ph = document.createElement("div");
    ph.className = "skipme-movie-no-image";
    ph.textContent = "🎬";
    posterWrap.appendChild(ph);
  }

  card.appendChild(posterWrap);

  // Footer: name + toggle
  const footer = document.createElement("div");
  footer.className = "skipme-movie-footer";

  const nameEl = document.createElement("div");
  nameEl.className = "skipme-movie-name";
  const displayName = movie.Name ?? "Unknown Movie";
  nameEl.textContent = displayName;
  nameEl.title = displayName;

  const tog = createToggle(
    "skipme-movie-" + movie.Id,
    !isDisabled,
    (enabled) => {
      if (enabled) {
        activeDisabledMovieIds().delete(movie.Id);
        card.style.opacity = "";
      } else {
        activeDisabledMovieIds().add(movie.Id);
        card.style.opacity = "0.6";
      }
    },
    isDisabled ? "Movie disabled – click to enable" : "Movie enabled – click to disable",
  );

  footer.appendChild(nameEl);
  footer.appendChild(tog.element);
  card.appendChild(footer);

  return card;
}

function isLibraryEnabled(section: UnifiedSection): boolean {
  const allSeriesEnabled = section.seriesItems.every((series) => !activeDisabledSeriesIds().has(series.Id));
  const allMoviesEnabled = section.movieItems.every((movie) => !activeDisabledMovieIds().has(movie.Id));
  return allSeriesEnabled && allMoviesEnabled;
}

function setLibraryEnabled(section: UnifiedSection, enabled: boolean): void {
  for (const series of section.seriesItems) {
    if (enabled) {
      activeDisabledSeriesIds().delete(series.Id);
    } else {
      activeDisabledSeriesIds().add(series.Id);
    }
  }

  for (const movie of section.movieItems) {
    if (enabled) {
      activeDisabledMovieIds().delete(movie.Id);
    } else {
      activeDisabledMovieIds().add(movie.Id);
    }
  }
}

// ── Unified library sections render ────────────────────────────────────────────
function renderLibrarySections(): void {
  const sectionsEl = byId("skipme-library-sections");
  if (!sectionsEl) return;
  sectionsEl.innerHTML = "";
  filteredSeriesIds = new Set<string>();
  filteredMovieIds = new Set<string>();

  const q = filterQuery;
  let hasAny = false;

  for (const section of unifiedSections) {
    const filteredSeries = q
      ? section.seriesItems.filter((s) => (s.Name ?? "").toLowerCase().includes(q))
      : section.seriesItems;
    const filteredMovies = q
      ? section.movieItems.filter((m) => (m.Name ?? "").toLowerCase().includes(q))
      : section.movieItems;

    if (!filteredSeries.length && !filteredMovies.length) continue;
    hasAny = true;

    const sectionEl = document.createElement("div");
    sectionEl.className = "skipme-library-section";

    const header = document.createElement("div");
    header.className = "skipme-library-header";

    const heading = document.createElement("h4");
    heading.className = "skipme-library-title";
    heading.textContent = section.libraryName;
    header.appendChild(heading);

    const libraryEnabled = isLibraryEnabled(section);
    const libraryToggle = createToggle(
      "skipme-library-" + section.libraryId,
      libraryEnabled,
      (enabled) => {
        setLibraryEnabled(section, enabled);
        renderLibrarySections();
      },
      libraryEnabled ? "Library enabled – click to disable" : "Library disabled – click to enable",
    );
    header.appendChild(libraryToggle.element);
    sectionEl.appendChild(header);

    if (filteredSeries.length) {
      const list = document.createElement("div");
      list.className = "skipme-series-list";
      const frag = document.createDocumentFragment();
      for (const s of filteredSeries) {
        filteredSeriesIds.add(s.Id);
        frag.appendChild(createSeriesCard(s));
      }
      list.appendChild(frag);
      sectionEl.appendChild(list);
    }

    if (filteredMovies.length) {
      const grid = document.createElement("div");
      grid.className = "skipme-movies-grid";
      for (const m of filteredMovies) {
        filteredMovieIds.add(m.Id);
        grid.appendChild(createMovieCard(m));
      }
      sectionEl.appendChild(grid);
    }

    sectionsEl.appendChild(sectionEl);
  }

  if (hasAny) {
    hide("skipme-empty");
    show("skipme-content-container");
  } else {
    hide("skipme-content-container");
    show("skipme-empty");
  }
}

function isSkipMeEnabledForLibrary(
  library: LibraryView,
  virtualFoldersById: Map<string, VirtualFolderInfo>,
  virtualFoldersByName: Map<string, VirtualFolderInfo>,
): boolean {
  const byId = virtualFoldersById.get(library.Id);
  const byName = virtualFoldersByName.get((library.Name ?? "").toLowerCase());
  const folder = byId ?? byName;

  if (!folder) {
    return true;
  }

  const disabledProviders = new Set(
    (folder.LibraryOptions?.DisabledMediaSegmentProviders ?? [])
      .map((p) => p?.toLowerCase())
      .filter((p): p is string => !!p),
  );

  return !disabledProviders.has(SKIPME_PROVIDER_ID) && !disabledProviders.has(SKIPME_PROVIDER_NAME);
}

// ── Initialisation ─────────────────────────────────────────────────────────────
function init(): void {
  if (initRunning) return;
  initRunning = true;
  show("skipme-loading");
  hide("skipme-error");
  hide("skipme-empty");
  hide("skipme-content-container");

  // Wrap in Promise.resolve() so any synchronous throw (e.g. ApiClient method
  // not yet available) becomes a caught rejection and the finally always runs.
  Promise.resolve()
    .then(async () => {
      const [config, libraries, virtualFolders] = await Promise.all([
        loadConfig(),
        fetchLibraries().catch(() => [] as LibraryView[]),
        fetchVirtualFolders().catch(() => [] as VirtualFolderInfo[]),
      ]);

      disabledSeriesIds = new Set(config.DisabledSeriesIds ?? []);
      disabledSeasonIds = new Set(config.DisabledSeasonIds ?? []);
      disabledMovieIds = new Set(config.DisabledMovieIds ?? []);
      enabledSpecialsSeasonIds = new Set(config.EnabledSpecialsSeasonIds ?? []);
      filterQuery = "";

      const searchEl = byId<HTMLInputElement>("skipme-search");
      if (searchEl) searchEl.value = "";

      const virtualFoldersById = new Map<string, VirtualFolderInfo>();
      const virtualFoldersByName = new Map<string, VirtualFolderInfo>();
      for (const folder of virtualFolders) {
        const itemId = folder.ItemId;
        if (itemId) {
          virtualFoldersById.set(itemId, folder);
        }

        const name = (folder.Name ?? "").toLowerCase();
        if (name) {
          virtualFoldersByName.set(name, folder);
        }
      }

      const enabledLibraries = libraries.filter((lib) =>
        isSkipMeEnabledForLibrary(lib, virtualFoldersById, virtualFoldersByName));

      // ── Fetch items per library ──────────────────────────────────────────────
      // Passing ParentId=<lib.Id> to the Items query ensures Jellyfin resolves
      // the virtual view ID correctly, avoiding the mismatch that occurs when
      // trying to match item.ParentId against view IDs after a global fetch.
      const sectionPromises = enabledLibraries.map(async (lib) => {
        const ct = lib.CollectionType ?? null;
        let seriesItems: BaseItem[] = [];
        let seriesTotalCount = 0;
        let movieItems: BaseItem[] = [];
        let moviesTotalCount = 0;

        if (ct === "tvshows" || ct === null) {
          const r = await fetchSeriesForLibrary(lib.Id).catch(() => ({ items: [] as BaseItem[], total: 0 }));
          seriesItems = r.items;
          seriesTotalCount = r.total;
        }
        if (ct === "movies" || ct === null) {
          const r = await fetchMoviesForLibrary(lib.Id).catch(() => ({ items: [] as BaseItem[], total: 0 }));
          movieItems = r.items;
          moviesTotalCount = r.total;
        }

        return { lib, seriesItems, seriesTotalCount, movieItems, moviesTotalCount };
      });

      const sectionResults = await Promise.all(sectionPromises);

      // Build unified sections, skipping empty libraries.
      // Sort: tvshows first (0), movies second (1), other last (2).
      unifiedSections = sectionResults
        .filter(({ seriesItems, movieItems }) => seriesItems.length > 0 || movieItems.length > 0)
        .map(({ lib, seriesItems, seriesTotalCount, movieItems, moviesTotalCount }) => ({
          libraryId: lib.Id,
          libraryName: lib.Name ?? "Library",
          collectionType: lib.CollectionType ?? null,
          seriesItems,
          seriesTotalCount,
          movieItems,
          moviesTotalCount,
        }));

      unifiedSections.sort((a, b) => {
        const order = (ct: string | null): number =>
          ct === "tvshows" ? 0 : ct === "movies" ? 1 : 2;
        return order(a.collectionType) - order(b.collectionType);
      });

      // Share tab always starts with every item disabled on each fresh page mount.
      // This runs once here in init(); search/filter operations only call
      // renderLibrarySections() and never reset this per-mount share state.
      shareDisabledSeriesIds = new Set(
        unifiedSections.flatMap((section) => section.seriesItems.map((series) => series.Id)),
      );
      shareDisabledMovieIds = new Set(
        unifiedSections.flatMap((section) => section.movieItems.map((movie) => movie.Id)),
      );

      // Show a truncation note if any library returned fewer items than it has.
      const anySeriesTruncated = unifiedSections.some((s) => s.seriesTotalCount > s.seriesItems.length);
      const anyMoviesTruncated = unifiedSections.some((s) => s.moviesTotalCount > s.movieItems.length);

      const noteEl = byId("skipme-truncation-note");
      if (noteEl) {
        if (anySeriesTruncated) {
          noteEl.textContent =
            "Some TV series libraries are very large; not all series could be loaded. Use the search bar to find hidden series.";
          noteEl.style.display = "";
        } else {
          noteEl.style.display = "none";
        }
      }

      const movieNoteEl = byId("skipme-movie-truncation-note");
      if (movieNoteEl) {
        if (anyMoviesTruncated) {
          movieNoteEl.textContent =
            "Some movie libraries are very large; not all movies could be loaded. Use the search bar to find hidden movies.";
          movieNoteEl.style.display = "";
        } else {
          movieNoteEl.style.display = "none";
        }
      }

      renderLibrarySections();
    })
    .catch((err: unknown) => {
      console.error("[SkipMe.db] Failed to initialise settings page:", err);
      show("skipme-error");
    })
    .finally(() => {
      initRunning = false;
      hide("skipme-loading");
    });
}

// ── Save ───────────────────────────────────────────────────────────────────────
function save(): void {
  const btn = byId<HTMLButtonElement>("skipme-save-btn");
  if (!btn) return;
  btn.disabled = true;
  setStatus("Saving…", "");

  loadConfig()
    .then((config) => {
      config.DisabledSeriesIds = Array.from(disabledSeriesIds);
      config.DisabledSeasonIds = Array.from(disabledSeasonIds);
      config.DisabledMovieIds = Array.from(disabledMovieIds);
      config.EnabledSpecialsSeasonIds = Array.from(enabledSpecialsSeasonIds);
      return saveConfig(config);
    })
    .then(() => setStatus("Settings saved.", "ok"))
    .catch((err: unknown) => {
      console.error("[SkipMe.db] Failed to save settings:", err);
      setStatus("Failed to save — please try again.", "err");
    })
    .finally(() => {
      if (btn) btn.disabled = false;
    });
}

function share(): void {
  const btn = byId<HTMLButtonElement>("skipme-share-btn");
  if (!btn) return;

  btn.disabled = true;
  btn.classList.add("is-loading");
  setStatus("Sharing…", "");

  const payload: ShareSubmitRequest = {
    FilteredSeriesIds: Array.from(filteredSeriesIds),
    FilteredMovieIds: Array.from(filteredMovieIds),
    DisabledSeriesIds: Array.from(shareDisabledSeriesIds),
    DisabledSeasonIds: Array.from(shareDisabledSeasonIds),
    DisabledMovieIds: Array.from(shareDisabledMovieIds),
    EnabledSpecialsSeasonIds: Array.from(shareEnabledSpecialsSeasonIds),
  };

  shareEnabledItems(payload)
    .then((result) => {
      if (!result.Ok && !result.SharedSegments) {
        const suffix = result.Error ? ` ${result.Error}` : "";
        setStatus(`Share failed.${suffix}`, "err");
        return;
      }

      const message =
        `Shared ${result.SharedSegments} segment(s). ` +
        `Skipped ${result.SkippedAlreadyShared} already shared, ` +
        `${result.SkippedMissingMetadata} missing metadata, ` +
        `${result.SkippedNoSegments} without Intro Skipper timestamps.`;

      setStatus(message, "ok");
    })
    .catch((err: unknown) => {
      console.error("[SkipMe.db] Failed to share segments:", err);
      setStatus("Failed to share — please try again.", "err");
    })
    .finally(() => {
      btn.disabled = false;
      btn.classList.remove("is-loading");
    });
}

// ── Page HTML template ─────────────────────────────────────────────────────────
function buildPageHTML(): string {
  return `
    <div class="content-primary">
      <div class="sectionTitleContainer sectionTitleContainer-cards">
        <h2 class="sectionTitle">SkipMe.db – Settings</h2>
      </div>
      <p id="skipme-description" class="fieldDescription"></p>

      <div class="skipme-tabs" role="tablist" aria-label="SkipMe actions">
        <button id="skipme-tab-sync" type="button" class="skipme-tab-button is-active" role="tab" aria-selected="true">
          Sync
        </button>
        <button id="skipme-tab-share" type="button" class="skipme-tab-button" role="tab" aria-selected="false">
          Share
        </button>
      </div>

      <div id="skipme-error" class="skipme-message skipme-error" style="display:none">
        <p>⚠ Failed to load library data. Please refresh the page.</p>
      </div>

      <div class="skipme-section">
        <div class="verticalSection skipme-filter-controls">
          <div class="inputContainer">
            <label class="inputLabel inputLabelUnfocused" for="skipme-search">Filter items</label>
            <input id="skipme-search" type="search" is="emby-input" class="emby-input"
                   autocomplete="off" placeholder="Start typing a name…" />
          </div>
        </div>

        <div id="skipme-loading" class="skipme-loading">
          <div class="skipme-spinner"></div>
          <p>Loading your library…</p>
        </div>

        <div id="skipme-empty" class="skipme-message" style="display:none">
          <p>No content found in your library.</p>
        </div>

        <div id="skipme-content-container" style="display:none">
          <p id="skipme-truncation-note" class="skipme-truncation-note" style="display:none"></p>
          <p id="skipme-movie-truncation-note" class="skipme-truncation-note" style="display:none"></p>
          <div id="skipme-library-sections"></div>
        </div>
      </div>

      <div class="skipme-footer">
        <button id="skipme-save-btn" is="emby-button" type="button"
                class="raised button-submit emby-button">Save Settings</button>
        <button id="skipme-share-btn" is="emby-button" type="button"
                class="raised button-submit emby-button" style="display:none">Share Enabled Items</button>
        <span id="skipme-status" class="skipme-status"></span>
      </div>
    </div>`;
}

// ── Event wiring ───────────────────────────────────────────────────────────────
function wireEvents(): void {
  if (eventsWired) return;

  const searchEl = byId<HTMLInputElement>("skipme-search");
  const saveBtn = byId<HTMLButtonElement>("skipme-save-btn");
  const shareBtn = byId<HTMLButtonElement>("skipme-share-btn");
  const syncTabBtn = byId<HTMLButtonElement>("skipme-tab-sync");
  const shareTabBtn = byId<HTMLButtonElement>("skipme-tab-share");
  if (!searchEl || !saveBtn || !shareBtn || !syncTabBtn || !shareTabBtn) return;

  eventsWired = true;

  searchEl.addEventListener("input", (e) => {
    window.clearTimeout(searchDebounce);
    const query = (e.target as HTMLInputElement).value.trim().toLowerCase();
    searchDebounce = window.setTimeout(() => {
      filterQuery = query;
      renderLibrarySections();
    }, 150);
  });

  saveBtn.addEventListener("click", save);
  shareBtn.addEventListener("click", share);
  syncTabBtn.addEventListener("click", () => setActiveTab("sync"));
  shareTabBtn.addEventListener("click", () => setActiveTab("share"));
}

// ── Page mount / unmount ───────────────────────────────────────────────────────
let destroyPage: (() => void) | null = null;

function mountPage(rootEl: HTMLElement): void {
  destroyPage?.();
  destroyPage = null;

  // Reset all page-level state so a back-navigation starts fresh.
  eventsWired = false;
  initRunning = false;
  disabledSeriesIds = new Set();
  disabledSeasonIds = new Set();
  disabledMovieIds = new Set();
  enabledSpecialsSeasonIds = new Set();
  shareDisabledSeriesIds = new Set();
  shareDisabledSeasonIds = new Set();
  shareDisabledMovieIds = new Set();
  shareEnabledSpecialsSeasonIds = new Set();
  unifiedSections = [];
  filterQuery = "";
  activeTab = "sync";
  filteredSeriesIds = new Set();
  filteredMovieIds = new Set();
  seasonCache.clear();

  rootEl.innerHTML = buildPageHTML();
  setActiveTab(activeTab);
  wireEvents();
  init();

  destroyPage = () => {
    window.clearTimeout(searchDebounce);
  };
}

function unmountPage(): void {
  destroyPage?.();
  destroyPage = null;
}

// ── Page lifecycle binding ─────────────────────────────────────────────────────
function bindPage(rootEl: HTMLElement): void {
  if (rootEl.dataset["skipmebound"] === "true") return;
  rootEl.dataset["skipmebound"] = "true";

  // Walk up to the nearest .page / [data-role=page] element to attach lifecycle
  // events; fall back to rootEl itself if the ancestor isn't found.
  const page: HTMLElement =
    rootEl.closest<HTMLElement>(".page") ??
    rootEl.closest<HTMLElement>("[data-role='page']") ??
    rootEl;

  // 'pageshow'  – Jellyfin ≤ 10.8 (legacy Emby routing)
  // 'viewshow'  – Jellyfin 10.9+ (view-manager routing)
  page.addEventListener("pageshow", () => mountPage(rootEl));
  page.addEventListener("viewshow", () => mountPage(rootEl));
  page.addEventListener("pagehide", unmountPage);
  page.addEventListener("viewhide", unmountPage);

  mountPage(rootEl);
}

// ── Entry point ────────────────────────────────────────────────────────────────
function findAndBind(): boolean {
  const rootEl = document.querySelector<HTMLElement>(ROOT_SELECTOR);
  if (!rootEl) return false;
  bindPage(rootEl);
  return true;
}

if (!findAndBind()) {
  // Jellyfin injects the page HTML after the script runs; wait for it.
  const observer = new MutationObserver(() => {
    if (findAndBind()) {
      observer.disconnect();
      window.clearTimeout(observerTimeoutId);
    }
  });

  observer.observe(document.body ?? document.documentElement, {
    childList: true,
    subtree: true,
  });

  const observerTimeoutId = window.setTimeout(
    () => observer.disconnect(),
    OBSERVER_TIMEOUT_MS,
  );
}
