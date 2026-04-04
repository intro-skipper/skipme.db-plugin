import "./styles/main.css";

import type { BaseItem } from "./types.ts";
import { fetchSeasons, fetchSeries, getImageUrl, loadConfig, saveConfig } from "./api.ts";

// ── Constants ──────────────────────────────────────────────────────────────────
const ROOT_SELECTOR = "#skipme-root";
const OBSERVER_TIMEOUT_MS = 30_000;

// ── Page-level mutable state (reset on every mount) ───────────────────────────
let disabledSeriesIds = new Set<string>();
let disabledSeasonIds = new Set<string>();
let allSeries: BaseItem[] = [];
let filterQuery = "";
const seasonCache = new Map<string, BaseItem[]>();
let searchDebounce = 0;
let initRunning = false;
let eventsWired = false;

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

// ── Season card ────────────────────────────────────────────────────────────────
function createSeasonCard(season: BaseItem, seriesId: string): HTMLElement {
  const seriesDisabled = disabledSeriesIds.has(seriesId);
  const seasonDisabled = disabledSeasonIds.has(season.Id);

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
      if (enabled) {
        disabledSeasonIds.delete(season.Id);
        card.style.opacity = "";
      } else {
        disabledSeasonIds.add(season.Id);
        card.style.opacity = "0.6";
      }
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

  const grid = document.createElement("div");
  grid.className = "skipme-seasons-grid";
  for (const season of seasons) {
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
  const isDisabled = disabledSeriesIds.has(series.Id);

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
        disabledSeriesIds.delete(series.Id);
        hint.textContent = "Expand to manage individual seasons";
        hint.className = "skipme-series-hint";
      } else {
        disabledSeriesIds.add(series.Id);
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

// ── Series list ────────────────────────────────────────────────────────────────
function renderSeriesList(): void {
  const list = byId("skipme-series-list");
  if (!list) return;
  list.innerHTML = "";

  const q = filterQuery;
  const filtered = q
    ? allSeries.filter((s) => (s.Name ?? "").toLowerCase().includes(q))
    : allSeries;

  if (!filtered.length) {
    hide("skipme-series-container");
    show("skipme-empty");
    return;
  }

  hide("skipme-empty");
  show("skipme-series-container");

  const frag = document.createDocumentFragment();
  for (const s of filtered) {
    frag.appendChild(createSeriesCard(s));
  }
  list.appendChild(frag);
}

// ── Initialisation ─────────────────────────────────────────────────────────────
function init(): void {
  if (initRunning) return;
  initRunning = true;
  show("skipme-loading");
  hide("skipme-error");
  hide("skipme-empty");
  hide("skipme-series-container");

  // Wrap in Promise.resolve() so any synchronous throw (e.g. ApiClient method
  // not yet available) becomes a caught rejection and the finally always runs.
  Promise.resolve()
    .then(async () => {
      const [config, { items, total }] = await Promise.all([loadConfig(), fetchSeries()]);

      disabledSeriesIds = new Set(config.DisabledSeriesIds ?? []);
      disabledSeasonIds = new Set(config.DisabledSeasonIds ?? []);
      allSeries = items;
      filterQuery = "";

      const searchEl = byId<HTMLInputElement>("skipme-search");
      if (searchEl) searchEl.value = "";

      const noteEl = byId("skipme-truncation-note");
      if (noteEl) {
        if (total > items.length) {
          noteEl.textContent =
            "Your library contains " +
            total +
            " series but only " +
            items.length +
            " could be loaded. Use the search bar to find hidden series.";
          noteEl.style.display = "";
        } else {
          noteEl.style.display = "none";
        }
      }

      renderSeriesList();
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

// ── Page HTML template ─────────────────────────────────────────────────────────
function buildPageHTML(): string {
  return `
    <div class="content-primary">
      <div class="sectionTitleContainer sectionTitleContainer-cards">
        <h2 class="sectionTitle">SkipMe.db – Series &amp; Season Settings</h2>
      </div>
      <p class="fieldDescription">
        Toggle crowd-sourced segment data on or off for individual series or seasons.
        Segments remain in the local database but will not be surfaced to Jellyfin when disabled.
      </p>

      <div class="verticalSection">
        <div class="inputContainer">
          <label class="inputLabel inputLabelUnfocused" for="skipme-search">Filter series</label>
          <input id="skipme-search" type="search" is="emby-input" class="emby-input"
                 autocomplete="off" placeholder="Start typing a series name…" />
        </div>
      </div>

      <div id="skipme-loading" class="skipme-loading">
        <div class="skipme-spinner"></div>
        <p>Loading your library…</p>
      </div>

      <div id="skipme-error" class="skipme-message skipme-error" style="display:none">
        <p>⚠ Failed to load library data. Please refresh the page.</p>
      </div>

      <div id="skipme-empty" class="skipme-message" style="display:none">
        <p>No TV series found in your library.</p>
      </div>

      <div id="skipme-series-container" style="display:none">
        <p id="skipme-truncation-note" class="skipme-truncation-note" style="display:none"></p>
        <div id="skipme-series-list"></div>
      </div>

      <div class="skipme-footer">
        <button id="skipme-save-btn" is="emby-button" type="button"
                class="raised button-submit emby-button">Save Settings</button>
        <span id="skipme-status" class="skipme-status"></span>
      </div>
    </div>`;
}

// ── Event wiring ───────────────────────────────────────────────────────────────
function wireEvents(): void {
  if (eventsWired) return;

  const searchEl = byId<HTMLInputElement>("skipme-search");
  const saveBtn = byId<HTMLButtonElement>("skipme-save-btn");
  if (!searchEl || !saveBtn) return;

  eventsWired = true;

  searchEl.addEventListener("input", (e) => {
    window.clearTimeout(searchDebounce);
    const query = (e.target as HTMLInputElement).value.trim().toLowerCase();
    searchDebounce = window.setTimeout(() => {
      filterQuery = query;
      renderSeriesList();
    }, 150);
  });

  saveBtn.addEventListener("click", save);
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
  allSeries = [];
  filterQuery = "";
  seasonCache.clear();

  rootEl.innerHTML = buildPageHTML();
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
