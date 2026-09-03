import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Auth } from '../../core/auth/auth';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  templateUrl: './login.html',
  styleUrl: './login.scss',
})
export class Login {
  tenantId = '3FA85F64-5717-4562-B3FC-2C963F66AFA6';
  email = '';
  password = '';
  readonly submitting = signal(false);
  readonly error = signal<string | null>(null);

  constructor(private readonly auth: Auth, private readonly router: Router) {}

  async submit(): Promise<void> {
    this.error.set(null);
    this.submitting.set(true);
    try {
      await this.auth.login(this.tenantId, this.email, this.password);
      await this.router.navigateByUrl('/agent-workspace');
    } catch {
      this.error.set('Login failed - check tenant id, email and password.');
    } finally {
      this.submitting.set(false);
    }
  }
}
