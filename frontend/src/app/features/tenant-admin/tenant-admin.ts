import { HttpClient } from '@angular/common/http';
import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { environment } from '../../../environments/environment';

/**
 * Section 1 target business outcome: "A tenant can be provisioned, create branches/teams/
 * agents, connect a SIP trunk and digital channels, and become operational without code
 * changes." This starter only wires up the first step (POST /api/tenants) - branches/teams/
 * agents/SIP trunk/channel account screens are TODOs for you to add the same way.
 */
@Component({
  selector: 'app-tenant-admin',
  imports: [FormsModule],
  templateUrl: './tenant-admin.html',
  styleUrl: './tenant-admin.scss',
})
export class TenantAdmin {
  name = '';
  slug = '';
  edition: 'Voice' | 'Omnichannel' | 'Enterprise' = 'Voice';
  readonly submitting = signal(false);
  readonly result = signal<string | null>(null);

  constructor(private readonly http: HttpClient) {}

  provision(): void {
    this.submitting.set(true);
    this.result.set(null);

    this.http.post(`${environment.apiBaseUrl}/tenants`, {
      name: this.name,
      slug: this.slug,
      edition: this.edition
    }).subscribe({
      next: () => this.result.set(`Tenant "${this.name}" provisioned.`),
      error: (err) => this.result.set(`Failed: ${err.error ?? err.message}`),
      complete: () => this.submitting.set(false)
    });
  }
}
