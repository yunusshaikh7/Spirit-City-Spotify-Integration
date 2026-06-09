import path from "node:path";
import fs from "node:fs";

import dotenv from "dotenv";

const loadedEnvFiles: string[] = [];

loadEnvFiles();

export type AppConfig = {
  port: number;
  host: string;
  clientId: string;
  clientSecretProvided: boolean;
  spotifyUser: string;
  redirectUri: string;
  tokenStorePath: string;
  scopes: string[];
  loadedEnvFiles: string[];
};

const DEFAULT_PORT = 8012;
const DEFAULT_HOST = "127.0.0.1";

const scopes = [
  "streaming",
  "user-read-email",
  "user-read-private",
  "user-read-playback-state",
  "user-modify-playback-state",
  "user-read-currently-playing",
];

export function loadConfig(): AppConfig {
  const port = Number.parseInt(process.env.PORT ?? `${DEFAULT_PORT}`, 10);
  const host = process.env.HOST ?? DEFAULT_HOST;
  const clientId = firstDefined(
    process.env.SPOTIFY_CLIENT_ID,
    process.env.ClientID,
    process.env.CLIENT_ID,
  );
  const clientSecret = firstDefined(
    process.env.SPOTIFY_CLIENT_SECRET,
    process.env.ClientSecret,
    process.env.CLIENT_SECRET,
  );
  const spotifyUser = firstDefined(
    process.env.SPOTIFY_USER,
    process.env.SpotifyUser,
    process.env.SPOTIFY_USERNAME,
  );
  const redirectUri =
    process.env.SPOTIFY_REDIRECT_URI?.trim() ??
    `http://${DEFAULT_HOST}:${port}/auth/callback`;
  const tokenStorePath = path.resolve(
    process.cwd(),
    process.env.TOKEN_STORE_PATH?.trim() || ".spotify-tokens.json",
  );

  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error("PORT must be a valid TCP port between 1 and 65535.");
  }

  if (redirectUri.includes("localhost")) {
    throw new Error(
      "SPOTIFY_REDIRECT_URI must use 127.0.0.1 or [::1], not localhost.",
    );
  }

  return {
    port,
    host,
    clientId,
    clientSecretProvided: Boolean(clientSecret),
    spotifyUser,
    redirectUri,
    tokenStorePath,
    scopes,
    loadedEnvFiles,
  };
}

function loadEnvFiles(): void {
  const cwd = process.cwd();
  const baseEnvPath = path.resolve(cwd, ".env");
  loadEnvFile(baseEnvPath);

  const candidates = [path.resolve(cwd, "spirit-sync.env")];

  const installDir = process.env.SPIRIT_CITY_INSTALL_DIR?.trim();
  if (installDir) {
    candidates.push(path.resolve(installDir, "spirit-sync.env"));
  }

  const explicitEnvPath = process.env.SPIRIT_SYNC_ENV_PATH?.trim();
  if (explicitEnvPath) {
    candidates.push(path.resolve(explicitEnvPath));
  }

  for (const candidate of unique(candidates)) {
    loadEnvFile(candidate);
  }
}

function loadEnvFile(envPath: string): void {
  if (!fs.existsSync(envPath)) {
    return;
  }

  dotenv.config({ path: envPath, quiet: true });
  loadedEnvFiles.push(envPath);
}

function firstDefined(...values: Array<string | undefined>): string {
  return values.map((value) => value?.trim() ?? "").find(Boolean) ?? "";
}

function unique(values: string[]): string[] {
  return [...new Set(values)];
}
