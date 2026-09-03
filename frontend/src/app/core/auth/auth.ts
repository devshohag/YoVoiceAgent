import { HttpClient } from '@angular/common/http';
import { Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

export interface LoginResponse {
  accessToken: string;
  refreshToken: string;
}

const ACCESS_TOKEN_KEY = 'ccaas.accessToken';
const REFRESH_TOKEN_KEY = 'ccaas.refreshToken';

/**
 * Section 14 - Authentication: JWT access token + rotating refresh token. Tokens are kept
 * in localStorage for this starter (simplest to wire up end-to-end) - swap for an httpOnly
 * cookie + BFF pattern before any real production deployment if you want to reduce XSS
 * token-theft exposure.
 */
@Injectable({ providedIn: 'root' })
export class Auth {
  readonly isAuthenticated = signal<boolean>(!!localStorage.getItem(ACCESS_TOKEN_KEY));

  constructor(private readonly http: HttpClient) {}

  async login(tenantId: string, email: string, password: string): Promise<void> {
    const response = await firstValueFrom(
      this.http.post<LoginResponse>(`${environment.apiBaseUrl}/auth/login?tenantId=${tenantId}`, { email, password })
    );
    this.setTokens(response);
  }

  async refresh(): Promise<LoginResponse> {
    const refreshToken = localStorage.getItem(REFRESH_TOKEN_KEY);
    const response = await firstValueFrom(
      this.http.post<LoginResponse>(`${environment.apiBaseUrl}/auth/refresh`, JSON.stringify(refreshToken), {
        headers: { 'Content-Type': 'application/json' }
      })
    );
    this.setTokens(response);
    return response;
  }

  logout(): void {
    localStorage.removeItem(ACCESS_TOKEN_KEY);
    localStorage.removeItem(REFRESH_TOKEN_KEY);
    this.isAuthenticated.set(false);
  }

  getAccessToken(): string | null {
    return localStorage.getItem(ACCESS_TOKEN_KEY);
  }

  private setTokens(response: LoginResponse): void {
    localStorage.setItem(ACCESS_TOKEN_KEY, response.accessToken);
    localStorage.setItem(REFRESH_TOKEN_KEY, response.refreshToken);
    this.isAuthenticated.set(true);
  }
}
