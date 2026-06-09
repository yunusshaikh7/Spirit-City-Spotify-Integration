import crypto from "node:crypto";

export function createRandomString(byteLength = 64): string {
  return crypto.randomBytes(byteLength).toString("base64url");
}

export function createCodeChallenge(codeVerifier: string): string {
  return crypto
    .createHash("sha256")
    .update(codeVerifier)
    .digest("base64url");
}
