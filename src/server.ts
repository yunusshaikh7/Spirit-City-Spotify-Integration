import cookieParser from "cookie-parser";
import express from "express";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { loadConfig } from "./config.js";
import { createCodeChallenge, createRandomString } from "./pkce.js";
import { SpotifyApiError, SpotifyClient } from "./spotify.js";
import { TokenStore } from "./tokenStore.js";

type AsyncHandler = (
  request: express.Request,
  response: express.Response,
  next: express.NextFunction,
) => Promise<void>;

const config = loadConfig();
const app = express();
const tokenStore = new TokenStore(config.tokenStorePath);
const spotify = new SpotifyClient(config, tokenStore);
const publicPath = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
  "public",
);

app.use(cookieParser());
app.use(express.json());
app.use(express.static(publicPath));

app.get(["/spirit-sync", "/ingame"], (_request, response) => {
  response.sendFile(path.join(publicPath, "ingame.html"));
});

app.get(["/spirit-sync-setup", "/setup"], (_request, response) => {
  response.redirect("/login");
});

app.get("/login", (request, response) => {
  if (!config.clientId) {
    response.redirect("/?error=missing_client_id");
    return;
  }

  const state = createRandomString(24);
  const codeVerifier = createRandomString();
  const codeChallenge = createCodeChallenge(codeVerifier);

  response.cookie("spotify_state", state, {
    httpOnly: true,
    sameSite: "lax",
    maxAge: 10 * 60 * 1000,
  });
  response.cookie("spotify_code_verifier", codeVerifier, {
    httpOnly: true,
    sameSite: "lax",
    maxAge: 10 * 60 * 1000,
  });

  response.redirect(spotify.buildAuthorizeUrl(state, codeChallenge));
});

app.get("/api/config", (_request, response) => {
  response.json({
    hasClientId: Boolean(config.clientId),
    clientSecretProvided: config.clientSecretProvided,
    spotifyUser: config.spotifyUser,
    redirectUri: config.redirectUri,
    scopes: config.scopes,
    loadedEnvFiles: config.loadedEnvFiles,
  });
});

app.get(
  "/auth/callback",
  asyncRoute(async (request, response) => {
    const code = asString(request.query.code);
    const returnedState = asString(request.query.state);
    const storedState = request.cookies.spotify_state;
    const codeVerifier = request.cookies.spotify_code_verifier;

    if (!code) {
      throw new Error("Spotify callback did not include an authorization code.");
    }

    if (!returnedState || returnedState !== storedState) {
      throw new Error("Spotify callback state did not match.");
    }

    if (!codeVerifier) {
      throw new Error("Missing PKCE verifier cookie. Try logging in again.");
    }

    await spotify.exchangeCodeForTokens(code, codeVerifier);
    response.clearCookie("spotify_state");
    response.clearCookie("spotify_code_verifier");
    response.redirect("/");
  }),
);

app.post(
  "/logout",
  asyncRoute(async (_request, response) => {
    await tokenStore.clear();
    response.json({ ok: true });
  }),
);

app.get(
  "/api/status",
  asyncRoute(async (_request, response) => {
    const tokens = await spotify.getValidTokens();

    if (!tokens) {
      response.json({ connected: false, playback: null });
      return;
    }

    const playback = await spotify.request<Record<string, unknown>>(
      "/me/player",
    );
    response.json({
      connected: true,
      expiresAt: tokens.expiresAt,
      playback: playback.data,
    });
  }),
);

app.get(
  "/api/now-playing",
  asyncRoute(async (_request, response) => {
    const tokens = await spotify.getValidTokens();

    if (!tokens) {
      response.json({
        connected: false,
        playback: toNowPlaying(null),
      });
      return;
    }

    const playback = await spotify.request<SpotifyPlayback>("/me/player");
    response.json({
      connected: true,
      expiresAt: tokens.expiresAt,
      playback: toNowPlaying(playback.data),
    });
  }),
);

app.get(
  "/api/token",
  asyncRoute(async (_request, response) => {
    const tokens = await spotify.getValidTokens();

    if (!tokens) {
      response.status(401).json({ error: "not_connected" });
      return;
    }

    response.json({
      accessToken: tokens.accessToken,
      expiresAt: tokens.expiresAt,
    });
  }),
);

app.get(
  "/api/player/devices",
  asyncRoute(async (_request, response) => {
    const devices = await getSpotifyDevices();
    response.json({ devices });
  }),
);

app.post(
  "/api/player/transfer",
  asyncRoute(async (request, response) => {
    const deviceId = request.body?.deviceId;
    const play = Boolean(request.body?.play);

    if (!deviceId || typeof deviceId !== "string") {
      response.status(400).json({ error: "deviceId is required." });
      return;
    }

    await spotify.request("/me/player", {
      method: "PUT",
      body: JSON.stringify({
        device_ids: [deviceId],
        play,
      }),
    });
    response.json({ ok: true });
  }),
);

app.post(
  "/api/player/play",
  asyncRoute(async (_request, response) => {
    const deviceId = await resolvePlaybackDeviceId();

    try {
      await spotify.request(
        withQuery("/me/player/play", {
          device_id: deviceId,
        }),
        { method: "PUT" },
      );
      response.json({ ok: true, deviceId });
    } catch (error) {
      if (!deviceId) {
        throw error;
      }

      await spotify.request("/me/player", {
        method: "PUT",
        body: JSON.stringify({
          device_ids: [deviceId],
          play: true,
        }),
      });
      response.json({ ok: true, deviceId, transferred: true });
    }
  }),
);

app.post(
  "/api/player/pause",
  asyncRoute(async (_request, response) => {
    const deviceId = await resolvePlaybackDeviceId();
    await spotify.request(
      withQuery("/me/player/pause", {
        device_id: deviceId,
      }),
      { method: "PUT" },
    );
    response.json({ ok: true, deviceId });
  }),
);

app.post(
  "/api/player/next",
  asyncRoute(async (_request, response) => {
    const deviceId = await resolvePlaybackDeviceId();
    await spotify.request(
      withQuery("/me/player/next", {
        device_id: deviceId,
      }),
      { method: "POST" },
    );
    response.json({ ok: true, deviceId });
  }),
);

app.post(
  "/api/player/previous",
  asyncRoute(async (_request, response) => {
    const deviceId = await resolvePlaybackDeviceId();
    await spotify.request(
      withQuery("/me/player/previous", {
        device_id: deviceId,
      }),
      { method: "POST" },
    );
    response.json({ ok: true, deviceId });
  }),
);

app.post(
  "/api/player/shuffle",
  asyncRoute(async (request, response) => {
    const state = Boolean(request.body?.state);
    const deviceId = await resolvePlaybackDeviceId();
    await spotify.request(
      withQuery("/me/player/shuffle", {
        state,
        device_id: deviceId,
      }),
      {
        method: "PUT",
      },
    );
    response.json({ ok: true, state, deviceId });
  }),
);

app.post(
  "/api/player/repeat",
  asyncRoute(async (request, response) => {
    const state = request.body?.state;
    const normalizedState =
      state === "track" || state === "context" ? state : "off";

    const deviceId = await resolvePlaybackDeviceId();
    await spotify.request(
      withQuery("/me/player/repeat", {
        state: normalizedState,
        device_id: deviceId,
      }),
      {
        method: "PUT",
      },
    );
    response.json({ ok: true, state: normalizedState, deviceId });
  }),
);

app.use(
  (
    error: Error,
    _request: express.Request,
    response: express.Response,
    _next: express.NextFunction,
  ) => {
    const status = getErrorStatus(error);
    response.status(status).json({
      error: error.message,
    });
  },
);

app.listen(config.port, config.host, () => {
  const baseUrl = `http://${config.host}:${config.port}`;
  console.log(`Spirit Sync bridge listening at ${baseUrl}`);
  console.log(`Login URL: ${baseUrl}/login`);
});

function asyncRoute(handler: AsyncHandler): express.RequestHandler {
  return (request, response, next) => {
    void handler(request, response, next).catch(next);
  };
}

function getErrorStatus(error: Error): number {
  if (error.name === "UnauthorizedError") {
    return 401;
  }

  if (error instanceof SpotifyApiError) {
    return error.status >= 400 && error.status < 500 ? error.status : 502;
  }

  return 500;
}

function asString(value: unknown): string | null {
  return typeof value === "string" ? value : null;
}

type SpotifyPlayback = {
  is_playing?: boolean;
  shuffle_state?: boolean;
  repeat_state?: string;
  item?: {
    name?: string;
    artists?: Array<{ name?: string }>;
    album?: {
      name?: string;
      images?: Array<{ url?: string }>;
    };
  } | null;
};

type SpotifyDevice = {
  id?: string | null;
  is_active?: boolean;
  is_restricted?: boolean;
  name?: string;
  type?: string;
};

type SpotifyDevicesResponse = {
  devices?: SpotifyDevice[];
};

function toNowPlaying(playback: SpotifyPlayback | null) {
  const item = playback?.item;

  if (!item) {
    return {
      title: "Nothing playing",
      artist: "Spotify",
      album: "",
      albumArt: "",
      isPlaying: false,
      shuffle: false,
      repeat: "off",
    };
  }

  return {
    title: item.name ?? "Unknown track",
    artist:
      item.artists
        ?.map((artist) => artist.name)
        .filter(Boolean)
        .join(", ") || "Unknown artist",
    album: item.album?.name ?? "",
    albumArt: item.album?.images?.[0]?.url ?? "",
    isPlaying: Boolean(playback?.is_playing),
    shuffle: Boolean(playback?.shuffle_state),
    repeat: playback?.repeat_state ?? "off",
  };
}

async function resolvePlaybackDeviceId(): Promise<string | null> {
  const devices = await getSpotifyDevices();
  return pickPlaybackDevice(devices)?.id ?? null;
}

async function getSpotifyDevices(): Promise<SpotifyDevice[]> {
  const devices = await spotify.request<SpotifyDevicesResponse>(
    "/me/player/devices",
  );
  return devices.data?.devices ?? [];
}

function pickPlaybackDevice(devices: SpotifyDevice[]): SpotifyDevice | null {
  const usableDevices = devices.filter(
    (device) => device.id && !device.is_restricted,
  );

  return (
    usableDevices.find(
      (device) => device.type === "Computer" && device.is_active,
    ) ??
    usableDevices.find((device) => device.type === "Computer") ??
    usableDevices.find((device) => device.is_active) ??
    usableDevices[0] ??
    null
  );
}

function withQuery(
  endpoint: string,
  params: Record<string, string | number | boolean | null | undefined>,
): string {
  const url = new URL(endpoint, "https://api.spotify.com");

  for (const [key, value] of Object.entries(params)) {
    if (value !== null && value !== undefined && value !== "") {
      url.searchParams.set(key, `${value}`);
    }
  }

  return `${url.pathname}${url.search}`;
}
