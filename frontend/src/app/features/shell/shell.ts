import { Component } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Auth } from '../../core/auth/auth';

/**
 * Section 11 - Agent Workspace & Supervisor Center: this shell is the common nav shared by
 * the three main screens (agent workspace, supervisor dashboard, tenant admin) - each of
 * those routes below is lazy-loaded and currently a placeholder for you to build out.
 */
@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell {
  constructor(private readonly auth: Auth, private readonly router: Router) {}

  logout(): void {
    this.auth.logout();
    this.router.navigateByUrl('/login');
  }
}
