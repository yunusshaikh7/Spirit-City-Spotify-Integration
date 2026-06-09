let player = null;
let deviceId = null;
let connected = false;
let pollTimer = null;
let bridgeConfig = {
  hasClientId: false,
};
const isInGameMode = document.body.classList.contains("ingame");

const elements = {
  albumArt: document.querySelector("#albumArt"),
  connectionStatus: document.querySelector("#connectionStatus"),
  trackTitle: document.querySelector("#trackTitle"),
  trackArtist: document.querySelector("#trackArtist"),
  previousButton: document.querySelector("#previousButton"),
  playButton: document.querySelector("#playButton"),
  pauseButton: document.querySelector("#pauseButton"),
  nextButton: document.querySelector("#nextButton"),
  shuffleToggle: document.querySelector("#shuffleToggle"),
  repeatSelect: document.querySelector("#repeatSelect"),
  loginButton: document.querySelector("#loginButton"),
  logoutButton: document.querySelector("#logoutButton"),
};

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

elements.previousButton.addEventListener("click", () =>
  localPost("/api/player/previous"),
);
elements.playButton.addEventListener("click", () =>
  localPost("/api/player/play"),
);
elements.pauseButton.addEventListener("click", () =>
  localPost("/api/player/pause"),
);
elements.nextButton.addEventListener("click", () =>
  localPost("/api/player/next"),
);
elements.shuffleToggle.addEventListener("change", () =>
  localPost("/api/player/shuffle", {
    state: elements.shuffleToggle.checked,
  }),
);
elements.repeatSelect.addEventListener("change", () =>
  localPost("/api/player/repeat", {
    state: elements.repeatSelect.value,
  }),
);

window.onSpotifyWebPlaybackSDKReady = async () => {
  if (isInGameMode) {
    return;
  }

  await configReady;

  if (!bridgeConfig.hasClientId) {
    setMissingConfig();
    return;
  }

  const token = await getAccessToken();

  if (!token) {
    setDisconnected();
    return;
  }

  player = new Spotify.Player({
    name: "Spirit Sync",
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
    elements.connectionStatus.textContent = "Spotify Connect device ready.";
    await localPost("/api/player/transfer", {
      deviceId,
      play: false,
    });
    await refreshPlayback();
    startPolling(5000);
  });

  player.addListener("not_ready", () => {
    connected = false;
    elements.connectionStatus.textContent = "Spotify device went offline.";
  });

  player.addListener("player_state_changed", (state) => {
    if (state) {
      renderSdkState(state);
    }
  });

  for (const eventName of [
    "initialization_error",
    "authentication_error",
    "account_error",
    "playback_error",
  ]) {
    player.addListener(eventName, ({ message }) => {
      elements.connectionStatus.textContent = message;
    });
  }

  await player.connect();
};

async function initializePlayer() {
  await configReady;

  if (!bridgeConfig.hasClientId) {
    return;
  }

  if (isInGameMode) {
    elements.connectionStatus.textContent = "Checking Spotify playback...";
    await refreshNowPlaying();
    startPolling(3000);
    return;
  }

  // If the SDK is blocked or unsupported, keep the page useful as a remote.
  window.setTimeout(() => {
    if (!player && !connected) {
      elements.connectionStatus.textContent =
        "Spotify speaker unavailable. Using remote control mode.";
      void refreshNowPlaying();
      startPolling(5000);
    }
  }, 4000);
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

async function localPost(url, body = {}) {
  const response = await fetch(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify(body),
  });

  if (!response.ok) {
    const payload = await response.json().catch(() => ({}));
    elements.connectionStatus.textContent =
      payload.error ?? `Request failed: ${response.status}`;
    return;
  }

  await refreshCurrentState();
}

async function refreshCurrentState() {
  if (isInGameMode || !player) {
    await refreshNowPlaying();
    return;
  }

  await refreshPlayback();
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

  elements.connectionStatus.textContent = playback.is_playing
    ? "Playing through Spotify."
    : "Spotify is paused.";

  publishNowPlaying({
    title,
    artist,
    album,
    albumArt: imageUrl,
    isPlaying: Boolean(playback.is_playing),
  });
}

function renderNowPlaying(playback) {
  const title = playback?.title ?? "Nothing playing";
  const artist = playback?.artist ?? "Spotify";
  const album = playback?.album ?? "";
  const imageUrl = playback?.albumArt ?? "";
  const isPlaying = Boolean(playback?.isPlaying);

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
      ? "Spotify is connected. Start playback in Spotify."
      : isPlaying
        ? "Playing through Spotify."
        : "Spotify is paused.";

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
  elements.connectionStatus.textContent = state.paused
    ? "Spotify is paused."
    : "Playing through Spotify.";

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
