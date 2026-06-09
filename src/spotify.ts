import type { AppConfig } from "./config.js";
import type { StoredTokens, TokenStore } from "./tokenStore.js";

type TokenResponse = {
  access_token: string;
  refresh_token?: string;
  token_type: string;
  scope: string;
  expires_in: number;
};

export class SpotifyClient {
  constructor(
    private readonly config: AppConfig,
    private readonly tokenStore: TokenStore,
  ) {}

  buildAuthorizeUrl(state: string, codeChallenge: string): string {
    this.ensureClientId();

    const authUrl = new URL("https://accounts.spotify.com/authorize");
    authUrl.search = new URLSearchParams({
      response_type: "code",
      client_id: this.config.clientId,
      scope: this.config.scopes.join(" "),
      redirect_uri: this.config.redirectUri,
      state,
      code_challenge_method: "S256",
      code_challenge: codeChallenge,
    }).toString();

    return authUrl.toString();
  }

  async exchangeCodeForTokens(
    code: string,
    codeVerifier: string,
  ): Promise<StoredTokens> {
    this.ensureClientId();

    const response = await fetch("https://accounts.spotify.com/api/token", {
      method: "POST",
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: new URLSearchParams({
        grant_type: "authorization_code",
        code,
        redirect_uri: this.config.redirectUri,
        client_id: this.config.clientId,
        code_verifier: codeVerifier,
      }),
    });

    const payload = await readSpotifyResponse<TokenResponse>(response);
    const tokens = toStoredTokens(payload);
    await this.tokenStore.write(tokens);
    return tokens;
  }

  async getValidTokens(): Promise<StoredTokens | null> {
    const tokens = await this.tokenStore.read();

    if (!tokens) {
      return null;
    }

    if (tokens.expiresAt > Date.now() + 60_000) {
      return tokens;
    }

    return this.refreshTokens(tokens);
  }

  async refreshTokens(tokens: StoredTokens): Promise<StoredTokens> {
    this.ensureClientId();

    const response = await fetch("https://accounts.spotify.com/api/token", {
      method: "POST",
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: new URLSearchParams({
        grant_type: "refresh_token",
        refresh_token: tokens.refreshToken,
        client_id: this.config.clientId,
      }),
    });

    const payload = await readSpotifyResponse<TokenResponse>(response);
    const refreshed = toStoredTokens(payload, tokens.refreshToken);
    await this.tokenStore.write(refreshed);
    return refreshed;
  }

  async request<T>(
    endpoint: string,
    init: RequestInit = {},
  ): Promise<{ status: number; data: T | null }> {
    const tokens = await this.getValidTokens();

    if (!tokens) {
      const error = new Error("Spotify account is not connected.");
      error.name = "UnauthorizedError";
      throw error;
    }

    const response = await fetch(`https://api.spotify.com/v1${endpoint}`, {
      ...init,
      headers: {
        Authorization: `Bearer ${tokens.accessToken}`,
        "Content-Type": "application/json",
        ...init.headers,
      },
    });

    if (response.status === 204) {
      return { status: response.status, data: null };
    }

    return {
      status: response.status,
      data: await readSpotifyResponse<T>(response),
    };
  }

  private ensureClientId(): void {
    if (!this.config.clientId) {
      throw new Error("SPOTIFY_CLIENT_ID is required. Add it to your .env file.");
    }
  }
}

export class SpotifyApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(`Spotify API error ${status}: ${message}`);
    this.name = "SpotifyApiError";
  }
}

function toStoredTokens(
  payload: TokenResponse,
  existingRefreshToken?: string,
): StoredTokens {
  const refreshToken = payload.refresh_token ?? existingRefreshToken;

  if (!refreshToken) {
    throw new Error("Spotify did not return a refresh token.");
  }

  return {
    accessToken: payload.access_token,
    refreshToken,
    tokenType: payload.token_type,
    scope: payload.scope,
    expiresAt: Date.now() + payload.expires_in * 1000,
  };
}

async function readSpotifyResponse<T>(response: Response): Promise<T> {
  const text = await response.text();
  let payload: any = null;

  if (text) {
    try {
      payload = JSON.parse(text);
    } catch {
      if (response.ok) {
        return null as T;
      }

      const message = text.trim() || response.statusText;
      throw new SpotifyApiError(
        response.status,
        message.slice(0, 300) || "Unexpected non-JSON response.",
      );
    }
  }

  if (!response.ok) {
    const message =
      payload?.error_description ??
      payload?.error?.message ??
      payload?.error ??
      response.statusText;
    throw new SpotifyApiError(response.status, message);
  }

  return payload as T;
}
