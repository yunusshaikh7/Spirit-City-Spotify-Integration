let player = null;
let deviceId = null;
let connected = false;
let spotifyAuthenticated = false;
let pollTimer = null;
let sdkDeviceUnavailable = false;
let deviceUnavailableMessage = "";
let selectedDeviceId = localStorage.getItem("spirit-sync.selectedDeviceId") || "";
let availableDevices = [];
let bridgeConfig = {
  hasClientId: false,
  spotifyDeviceName: "Spirit Sync",
  spotifyPlaybackMode: "remote",
};
const isInGameMode = document.body.classList.contains("ingame");
const SPOTIFY_SDK_URL = "https://sdk.scdn.co/spotify-player.js";
const SDK_LOAD_TIMEOUT_MS = isInGameMode ? 8000 : 5000;

const elements = {
  albumArt: document.querySelector("#albumArt"),
  connectionStatus: document.querySelector("#connectionStatus"),
  trackTitle: document.querySelector("#trackTitle"),
  trackArtist: document.querySelector("#trackArtist"),
  deviceOptions: document.querySelector("#deviceOptions"),
  refreshDevicesButton: document.querySelector("#refreshDevicesButton"),
  previousButton: document.querySelector("#previousButton"),
  playButton: document.querySelector("#playButton"),
  pauseButton: document.querySelector("#pauseButton"),
  nextButton: document.querySelector("#nextButton"),
  shuffleToggle: document.querySelector("#shuffleToggle"),
  repeatSelect: document.querySelector("#repeatSelect"),
  loginButton: document.querySelector("#loginButton"),
  logoutButton: document.querySelector("#logoutButton"),
};

setPlaybackControlsEnabled(false);

const configReady = loadBridgeConfig();
void initializePlayer();

elements.loginButton.addEventListener("click", () => {
  if (!bridgeConfig.hasClientId) {
    setMissingConfig();
    return;
  }

  window.location.href = "/login";
});

elements.logoutButton.addEventListener("click", async () => {
  await localPost("/logout");
  window.location.reload();
});

elements.refreshDevicesButton.addEventListener("click", () =>
  loadSpotifyDevices(),
);

elements.previousButton.addEventListener("click", () =>
  postPlayerCommand("/api/player/previous"),
);
elements.playButton.addEventListener("click", () =>
  postPlayerCommand("/api/player/play"),
);
elements.pauseButton.addEventListener("click", () =>
  postPlayerCommand("/api/player/pause"),
);
elements.nextButton.addEventListener("click", () =>
  postPlayerCommand("/api/player/next"),
);
elements.shuffleToggle.addEventListener("change", () =>
  postPlayerCommand("/api/player/shuffle", {
    state: elements.shuffleToggle.checked,
  }),
);
elements.repeatSelect.addEventListener("change", () =>
  postPlayerCommand("/api/player/repeat", {
    state: elements.repeatSelect.value,
  }),
);

async function initializePlayer() {
  await configReady;

  if (!bridgeConfig.hasClientId) {
    return;
  }

  const token = await getAccessToken();

  if (!token) {
    setDisconnected();
    return;
  }

  spotifyAuthenticated = true;
  await loadSpotifyDevices({ reportErrors: false });

  if (isRemoteControlMode()) {
    await startRemoteControl("Remote controlling Spotify on this PC.");
    return;
  }

  elements.connectionStatus.textContent = `Starting ${getSpotifyDeviceName()}...`;
  const sdkStarted = await startSpotifySdkPlayer();

  if (!sdkStarted) {
    if (isAutoPlaybackMode() || selectedDeviceId) {
      await startRemoteControl(
        getSelectedDeviceFallbackMessage(),
      );
      return;
    }

    setDeviceUnavailable(
      "Spotify Web Playback could not start in this browser.",
    );
    return;
  }

  // If CEF cannot complete SDK setup, do not fall back to the desktop app.
  window.setTimeout(() => {
    if (!deviceId) {
      if (isAutoPlaybackMode() || selectedDeviceId) {
        void startRemoteControl(
          getSelectedDeviceFallbackMessage(),
        );
        return;
      }

      setDeviceUnavailable(
        "Spotify Web Playback did not expose a Spirit Sync device.",
      );
    }
  }, SDK_LOAD_TIMEOUT_MS);
}

async function startSpotifySdkPlayer() {
  const sdkLoaded = await loadSpotifySdk();

  if (!sdkLoaded) {
    return false;
  }

  const playerName = getSpotifyDeviceName();

  player = new window.Spotify.Player({
    name: playerName,
    getOAuthToken: async (callback) => {
      const freshToken = await getAccessToken();
      if (freshToken) {
        callback(freshToken);
      }
    },
    volume: 0.5,
  });

  player.addListener("ready", async ({ device_id }) => {
    deviceId = device_id;
    connected = true;
    sdkDeviceUnavailable = false;
    deviceUnavailableMessage = "";
    syncPlaybackControls();
    elements.connectionStatus.textContent = `${playerName} device ready.`;
    await transferToSdkDevice(false);
    await loadSpotifyDevices({ reportErrors: false });
    await refreshCurrentState();
    startPolling(isInGameMode ? 3000 : 5000);
  });

  player.addListener("not_ready", () => {
    connected = false;
    deviceId = null;
    syncPlaybackControls();
    elements.connectionStatus.textContent = `${playerName} device went offline.`;
  });

  player.addListener("player_state_changed", (state) => {
    if (state) {
      renderSdkState(state);
    }
  });

  const blockingEvents = new Set([
    "initialization_error",
    "authentication_error",
    "account_error",
  ]);

  for (const eventName of [
    ...blockingEvents,
    "playback_error",
  ]) {
    player.addListener(eventName, ({ message }) => {
      elements.connectionStatus.textContent = message;
      if (blockingEvents.has(eventName)) {
        if (isAutoPlaybackMode() || selectedDeviceId) {
          void startRemoteControl(
            getSelectedDeviceFallbackMessage(),
          );
          return;
        }

        setDeviceUnavailable(
          message || "Spotify Web Playback cannot create a Spirit Sync device.",
        );
      }
    });
  }

  const sdkConnected = await player.connect();

  if (!sdkConnected) {
    player = null;
    return false;
  }

  return true;
}

function loadSpotifySdk() {
  if (window.Spotify?.Player) {
    return Promise.resolve(true);
  }

  return new Promise((resolve) => {
    let resolved = false;
    let timeout = 0;
    const finish = (loaded) => {
      if (resolved) {
        return;
      }

      resolved = true;
      window.clearTimeout(timeout);
      resolve(Boolean(loaded && window.Spotify?.Player));
    };

    const previousReady = window.onSpotifyWebPlaybackSDKReady;
    window.onSpotifyWebPlaybackSDKReady = () => {
      if (typeof previousReady === "function") {
        previousReady();
      }

      finish(true);
    };

    let sdkScript = document.querySelector(`script[src="${SPOTIFY_SDK_URL}"]`);

    if (!sdkScript) {
      sdkScript = document.createElement("script");
      sdkScript.src = SPOTIFY_SDK_URL;
      sdkScript.async = true;
      sdkScript.dataset.spotifySdk = "true";
      document.body.appendChild(sdkScript);
    }

    sdkScript.addEventListener("error", () => finish(false), { once: true });
    timeout = window.setTimeout(
      () => finish(Boolean(window.Spotify?.Player)),
      SDK_LOAD_TIMEOUT_MS,
    );
  });
}

async function startRemoteControl(message) {
  player = null;
  deviceId = null;
  connected = false;
  sdkDeviceUnavailable = false;
  deviceUnavailableMessage = "";
  syncPlaybackControls();
  await refreshNowPlaying();

  if (!connected) {
    return;
  }

  if (message) {
    elements.connectionStatus.textContent = message;
  }

  startPolling(isInGameMode ? 3000 : 5000);
}

function setDeviceUnavailable(message) {
  player = null;
  deviceId = null;
  connected = false;
  sdkDeviceUnavailable = true;
  deviceUnavailableMessage = message;
  syncPlaybackControls();
  elements.connectionStatus.textContent = message;
  elements.trackTitle.textContent = "Spotify device unavailable";
  elements.trackArtist.textContent = "Spirit Sync cannot play through Spotify Desktop.";
  elements.albumArt.removeAttribute("src");
  publishNowPlaying({
    title: "Spotify device unavailable",
    artist: "Spirit Sync",
    album: "",
    albumArt: "",
    isPlaying: false,
  });
}

async function transferToSdkDevice(play) {
  if (!deviceId) {
    return false;
  }

  for (let attempt = 0; attempt < 3; attempt += 1) {
    const transferred = await localPost(
      "/api/player/transfer",
      {
        deviceId,
        play,
      },
      {
        refresh: false,
        reportErrors: attempt === 2,
      },
    );

    if (transferred) {
      return true;
    }

    await delay(500);
  }

  return false;
}

function getSpotifyDeviceName() {
  return bridgeConfig.spotifyDeviceName || "Spirit Sync";
}

function getSpotifyPlaybackMode() {
  return bridgeConfig.spotifyPlaybackMode || "remote";
}

function isDevicePlaybackMode() {
  return getSpotifyPlaybackMode() === "device";
}

function isRemoteControlMode() {
  return getSpotifyPlaybackMode() === "remote";
}

function isAutoPlaybackMode() {
  return getSpotifyPlaybackMode() === "auto";
}

function getPlaybackStatus(isPlaying, deviceName = "") {
  const fallbackName = deviceId ? getSpotifyDeviceName() : "Spotify";
  const name = deviceName || fallbackName;

  if (isConfiguredDeviceName(name)) {
    return isPlaying ? `Playing on ${name}.` : `${name} is paused.`;
  }

  if (deviceName) {
    return isPlaying ? `Playing through ${deviceName}.` : `${deviceName} is paused.`;
  }

  return isPlaying ? "Playing through Spotify." : "Spotify is paused.";
}

function getIdleStatus(deviceName = "") {
  if (selectedDeviceId) {
    return `${getSelectedDeviceName() || deviceName || "Selected device"} is ready.`;
  }

  if (isRemoteControlMode()) {
    return `${deviceName || "Spotify"} is ready for remote control.`;
  }

  if (isAutoPlaybackMode() && !deviceId) {
    return `${deviceName || "Spotify"} is ready for remote control.`;
  }

  if (sdkDeviceUnavailable) {
    return deviceUnavailableMessage;
  }

  return `${deviceName || "Spotify"} is connected. Start playback in Spotify.`;
}

function isConfiguredDeviceName(name) {
  return normalizeDeviceName(name) === normalizeDeviceName(getSpotifyDeviceName());
}

function normalizeDeviceName(name) {
  return `${name ?? ""}`.trim().toLocaleLowerCase();
}

function delay(ms) {
  return new Promise((resolve) => {
    window.setTimeout(resolve, ms);
  });
}

async function refreshPlayback() {
  const response = await fetch("/api/status");

  if (!response.ok) {
    setDisconnected();
    return;
  }

  const status = await response.json();

  if (!status.connected) {
    setDisconnected();
    return;
  }

  renderPlayback(status.playback);
}

async function refreshNowPlaying() {
  const response = await fetch("/api/now-playing", {
    cache: "no-store",
  });

  if (!response.ok) {
    setDisconnected();
    return;
  }

  const status = await response.json();

  if (!status.connected) {
    setDisconnected();
    return;
  }

  connected = true;
  renderNowPlaying(status.playback);
}

async function loadBridgeConfig() {
  const response = await fetch("/api/config");
  bridgeConfig = await response.json();

  if (!bridgeConfig.hasClientId) {
    setMissingConfig();
  }
}

async function getAccessToken() {
  const response = await fetch("/api/token");

  if (!response.ok) {
    return null;
  }

  const payload = await response.json();
  return payload.accessToken;
}

async function loadSpotifyDevices(options = {}) {
  const response = await fetch("/api/player/devices", {
    cache: "no-store",
  });

  if (!response.ok) {
    if (options.reportErrors !== false) {
      elements.connectionStatus.textContent =
        `Could not load Spotify devices: ${response.status}`;
    }
    renderDeviceOptions([]);
    return;
  }

  const payload = await response.json();
  availableDevices = Array.isArray(payload.devices) ? payload.devices : [];
  renderDeviceOptions(availableDevices);
}

function renderDeviceOptions(devices) {
  const usableDevices = devices.filter((device) => device.id);
  if (deviceId && !usableDevices.some((device) => device.id === deviceId)) {
    usableDevices.unshift({
      id: deviceId,
      is_active: true,
      is_restricted: false,
      name: getSpotifyDeviceName(),
      type: "Web Playback",
    });
  }
  const selectedStillExists =
    selectedDeviceId &&
    usableDevices.some((device) => device.id === selectedDeviceId);

  if (selectedDeviceId && !selectedStillExists) {
    selectedDeviceId = "";
    localStorage.removeItem("spirit-sync.selectedDeviceId");
  }

  elements.deviceOptions.replaceChildren();
  elements.deviceOptions.appendChild(
    createDeviceButton({
      id: "",
      is_active: false,
      is_restricted: false,
      name: getAutoDeviceLabel(),
      type: "",
    }),
  );

  for (const device of usableDevices) {
    elements.deviceOptions.appendChild(createDeviceButton(device));
  }

  syncPlaybackControls();
  updateDeviceStatusText();
}

function createDeviceButton(device) {
  const button = document.createElement("button");
  const deviceIdValue = device.id || "";
  button.type = "button";
  button.className = "device-option";
  button.dataset.deviceId = deviceIdValue;
  button.textContent = getDeviceOptionLabel(device);
  button.disabled = Boolean(device.is_restricted);
  button.setAttribute(
    "aria-pressed",
    `${deviceIdValue === selectedDeviceId}`,
  );

  if (deviceIdValue === selectedDeviceId) {
    button.classList.add("is-selected");
  }

  button.addEventListener("click", () => {
    setSelectedDeviceId(deviceIdValue);
  });

  return button;
}

function getDeviceOptionLabel(device) {
  if (!device.id) {
    return device.name || getAutoDeviceLabel();
  }

  return [
    device.name || "Unnamed device",
    device.type ? `(${device.type})` : "",
    device.is_active ? "active" : "",
    device.is_restricted ? "restricted" : "",
  ]
    .filter(Boolean)
    .join(" ");
}

function setSelectedDeviceId(nextDeviceId) {
  selectedDeviceId = nextDeviceId;

  if (selectedDeviceId) {
    localStorage.setItem("spirit-sync.selectedDeviceId", selectedDeviceId);
  } else {
    localStorage.removeItem("spirit-sync.selectedDeviceId");
  }

  for (const button of elements.deviceOptions.querySelectorAll(".device-option")) {
    const isSelected = button.dataset.deviceId === selectedDeviceId;
    button.classList.toggle("is-selected", isSelected);
    button.setAttribute("aria-pressed", `${isSelected}`);
  }

  syncPlaybackControls();
  updateDeviceStatusText();

  if (selectedDeviceId) {
    void refreshCurrentState();
  }
}

function getAutoDeviceLabel() {
  if (isDevicePlaybackMode()) {
    return `Auto (${getSpotifyDeviceName()})`;
  }

  if (isRemoteControlMode()) {
    return "Auto (PC Spotify)";
  }

  return "Auto";
}

function getSelectedPlaybackDeviceId() {
  return selectedDeviceId || deviceId || null;
}

function getSelectedDeviceName() {
  const id = getSelectedPlaybackDeviceId();
  const selectedDevice = availableDevices.find((device) => device.id === id);

  if (selectedDevice?.name) {
    return selectedDevice.name;
  }

  return id === deviceId ? getSpotifyDeviceName() : "";
}

function getSelectedDeviceFallbackMessage() {
  const selectedDeviceName = getSelectedDeviceName();

  if (selectedDeviceId && selectedDeviceName) {
    return `Selected ${selectedDeviceName}.`;
  }

  if (selectedDeviceId) {
    return "Selected Spotify device will be controlled.";
  }

  return "Spirit Sync device unavailable. Remote controlling Spotify on this PC.";
}

function updateDeviceStatusText() {
  if (!selectedDeviceId) {
    return;
  }

  const selectedDeviceName = getSelectedDeviceName();
  if (selectedDeviceName) {
    elements.connectionStatus.textContent = `Selected ${selectedDeviceName}.`;
  }
}

async function localPost(url, body = {}, options = {}) {
  if (url.startsWith("/api/player/")) {
    await activatePlaybackElement();
  }

  const requestBody = withPlaybackDevice(url, body);

  if (
    url.startsWith("/api/player/") &&
    isDevicePlaybackMode() &&
    !requestBody.deviceId
  ) {
    setDeviceUnavailable(
      `${getSpotifyDeviceName()} is not ready as a Spotify Connect device.`,
    );
    return false;
  }

  const response = await fetch(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify(requestBody),
  });

  if (!response.ok) {
    const payload = await response.json().catch(() => ({}));
    if (options.reportErrors !== false) {
      elements.connectionStatus.textContent =
        payload.error ?? `Request failed: ${response.status}`;
    }

    return false;
  }

  if (options.refresh !== false) {
    await refreshCurrentState();
  }

  return true;
}

async function postPlayerCommand(url, body = {}) {
  if (isDevicePlaybackMode() && !getSelectedPlaybackDeviceId()) {
    setDeviceUnavailable(
      `${getSpotifyDeviceName()} is not ready as a Spotify Connect device.`,
    );
    return false;
  }

  return localPost(url, body);
}

async function refreshCurrentState() {
  if (player && deviceId) {
    const state = await player.getCurrentState().catch(() => null);

    if (state) {
      renderSdkState(state);
      return;
    }

    await refreshPlayback();
    return;
  }

  await refreshNowPlaying();
}

async function activatePlaybackElement() {
  if (!player?.activateElement) {
    return;
  }

  try {
    await player.activateElement();
  } catch {
    // Some embedded browsers expose the method but still reject activation.
  }
}

function withPlaybackDevice(url, body) {
  const targetDeviceId = getSelectedPlaybackDeviceId();

  if (!url.startsWith("/api/player/") || !targetDeviceId || body.deviceId) {
    return body;
  }

  return {
    ...body,
    deviceId: targetDeviceId,
  };
}

function setPlaybackControlsEnabled(enabled) {
  for (const control of [
    elements.previousButton,
    elements.playButton,
    elements.pauseButton,
    elements.nextButton,
    elements.shuffleToggle,
    elements.repeatSelect,
  ]) {
    if (control) {
      control.disabled = !enabled;
    }
  }
}

function syncPlaybackControls() {
  setPlaybackControlsEnabled(
    spotifyAuthenticated &&
      Boolean(
        selectedDeviceId ||
          deviceId ||
          isRemoteControlMode() ||
          isAutoPlaybackMode(),
      ),
  );
}

function startPolling(intervalMs) {
  if (pollTimer) {
    window.clearInterval(pollTimer);
  }

  pollTimer = window.setInterval(() => {
    if (connected) {
      void refreshCurrentState();
    }
  }, intervalMs);
}

function renderPlayback(playback) {
  if (
    isDevicePlaybackMode() &&
    !selectedDeviceId &&
    !isPlaybackOnConfiguredDevice(playback)
  ) {
    renderDeviceWaiting();
    return;
  }

  const item = playback?.item;

  if (!item) {
    elements.trackTitle.textContent = "Nothing playing";
    elements.trackArtist.textContent = "Start playback from Spotify.";
    elements.albumArt.removeAttribute("src");
    elements.shuffleToggle.checked = false;
    elements.repeatSelect.value = "off";
    publishNowPlaying({
      title: "Nothing playing",
      artist: "Spotify",
      album: "",
      albumArt: "",
      isPlaying: false,
    });
    return;
  }

  const title = item.name ?? "Unknown track";
  const artist =
    item.artists?.map((artist) => artist.name).join(", ") ?? "Unknown artist";
  const album = item.album?.name ?? "";
  const imageUrl = item.album?.images?.[0]?.url ?? "";

  elements.trackTitle.textContent = title;
  elements.trackArtist.textContent = artist;
  elements.shuffleToggle.checked = Boolean(playback.shuffle_state);
  elements.repeatSelect.value = playback.repeat_state ?? "off";

  if (imageUrl) {
    elements.albumArt.src = imageUrl;
  } else {
    elements.albumArt.removeAttribute("src");
  }

  elements.connectionStatus.textContent = getPlaybackStatus(
    Boolean(playback.is_playing),
    playback?.device?.name ?? "",
  );

  publishNowPlaying({
    title,
    artist,
    album,
    albumArt: imageUrl,
    isPlaying: Boolean(playback.is_playing),
  });
}

function renderNowPlaying(playback) {
  if (
    isDevicePlaybackMode() &&
    !selectedDeviceId &&
    playback?.deviceName &&
    !isConfiguredDeviceName(playback.deviceName)
  ) {
    renderDeviceWaiting();
    return;
  }

  const title = playback?.title ?? "Nothing playing";
  const artist = playback?.artist ?? "Spotify";
  const album = playback?.album ?? "";
  const imageUrl = playback?.albumArt ?? "";
  const isPlaying = Boolean(playback?.isPlaying);
  const deviceName = playback?.deviceName ?? "";

  elements.trackTitle.textContent = title;
  elements.trackArtist.textContent = artist;
  elements.shuffleToggle.checked = Boolean(playback?.shuffle);
  elements.repeatSelect.value = playback?.repeat ?? "off";

  if (imageUrl) {
    elements.albumArt.src = imageUrl;
  } else {
    elements.albumArt.removeAttribute("src");
  }

  elements.connectionStatus.textContent =
    title === "Nothing playing"
      ? getIdleStatus(deviceName)
      : getPlaybackStatus(isPlaying, deviceName);

  publishNowPlaying({
    title,
    artist,
    album,
    albumArt: imageUrl,
    isPlaying,
  });
}

function renderSdkState(state) {
  const currentTrack = state.track_window.current_track;

  if (!currentTrack) {
    return;
  }

  const title = currentTrack.name ?? "Unknown track";
  const artist = currentTrack.artists
    .map((artist) => artist.name)
    .join(", ");
  const album = currentTrack.album?.name ?? "";
  const imageUrl = currentTrack.album?.images?.[0]?.url ?? "";

  elements.trackTitle.textContent = title;
  elements.trackArtist.textContent = artist;
  elements.connectionStatus.textContent = getPlaybackStatus(
    !state.paused,
    getSpotifyDeviceName(),
  );

  if (imageUrl) {
    elements.albumArt.src = imageUrl;
  }

  publishNowPlaying({
    title,
    artist,
    album,
    albumArt: imageUrl,
    isPlaying: !state.paused,
  });
}

function setDisconnected() {
  connected = false;
  spotifyAuthenticated = false;
  sdkDeviceUnavailable = false;
  deviceUnavailableMessage = "";
  syncPlaybackControls();
  elements.connectionStatus.textContent = "Spotify is not connected.";
  elements.trackTitle.textContent = "Log in required";
  elements.trackArtist.textContent = "Use your Spotify developer app.";
  elements.albumArt.removeAttribute("src");
  publishNowPlaying({
    title: "Log in required",
    artist: "Spotify",
    album: "",
    albumArt: "",
    isPlaying: false,
  });
}

function setMissingConfig() {
  connected = false;
  spotifyAuthenticated = false;
  sdkDeviceUnavailable = false;
  deviceUnavailableMessage = "";
  syncPlaybackControls();
  elements.connectionStatus.textContent =
    "Add SPOTIFY_CLIENT_ID to .env, then restart the bridge.";
  elements.trackTitle.textContent = "Spotify app not configured";
  elements.trackArtist.textContent =
    bridgeConfig.redirectUri ?? "Use the 127.0.0.1 redirect URI.";
  elements.loginButton.disabled = true;
  elements.albumArt.removeAttribute("src");
  publishNowPlaying({
    title: "Spotify app not configured",
    artist: "Spirit Sync",
    album: "",
    albumArt: "",
    isPlaying: false,
  });
}

function isPlaybackOnConfiguredDevice(playback) {
  const playbackDeviceName = playback?.device?.name ?? "";

  if (!playbackDeviceName) {
    return true;
  }

  return isConfiguredDeviceName(playbackDeviceName);
}

function renderDeviceWaiting() {
  const deviceName = getSpotifyDeviceName();
  elements.trackTitle.textContent = `${deviceName} ready`;
  elements.trackArtist.textContent = "Select it in Spotify Connect or press Play.";
  elements.albumArt.removeAttribute("src");
  elements.connectionStatus.textContent = `${deviceName} is waiting for playback.`;
  publishNowPlaying({
    title: `${deviceName} ready`,
    artist: "Spirit Sync",
    album: "",
    albumArt: "",
    isPlaying: false,
  });
}

function publishNowPlaying(detail) {
  const payload = {
    ...detail,
    updatedAt: new Date().toISOString(),
  };

  window.spiritSyncNowPlaying = payload;
  document.title =
    payload.title && payload.artist
      ? `${payload.title} - ${payload.artist}`
      : "Spirit Sync";
  document.body.dataset.spiritSyncTitle = payload.title;
  document.body.dataset.spiritSyncArtist = payload.artist;
  document.body.dataset.spiritSyncPlaying = `${payload.isPlaying}`;
  localStorage.setItem("spirit-sync.nowPlaying", JSON.stringify(payload));
  window.dispatchEvent(
    new CustomEvent("spirit-sync:now-playing", {
      detail: payload,
    }),
  );
}
