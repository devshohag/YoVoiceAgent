import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth-guard';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./features/login/login').then(m => m.Login) },
  {
    path: '',
    loadComponent: () => import('./features/shell/shell').then(m => m.Shell),
    canActivate: [authGuard],
    children: [
      { path: '', redirectTo: 'agent-workspace', pathMatch: 'full' },
      {
        path: 'agent-workspace',
        loadComponent: () => import('./features/agent-workspace/agent-workspace').then(m => m.AgentWorkspace)
      },
      {
        path: 'supervisor',
        loadComponent: () => import('./features/supervisor/supervisor').then(m => m.Supervisor)
      },
      {
        path: 'tenant-admin',
        loadComponent: () => import('./features/tenant-admin/tenant-admin').then(m => m.TenantAdmin)
      },
      {
        path: 'ai-agents',
        loadComponent: () => import('./features/ai-agents/ai-agents').then(m => m.AiAgents)
      },
      {
        path: 'ai-agent-test',
        loadComponent: () => import('./features/ai-agent-test/ai-agent-test').then(m => m.AiAgentTest)
      },
      {
        path: 'voice-bridge',
        loadComponent: () => import('./features/voice-bridge/voice-bridge').then(m => m.VoiceBridge)
      },
      {
        path: 'appointment-master',
        loadComponent: () => import('./features/appointment-master/appointment-master').then(m => m.AppointmentMaster)
      },
      {
        path: 'master-data',
        loadComponent: () => import('./features/master-data/master-data').then(m => m.MasterData)
      }
    ]
  },
  { path: '**', redirectTo: '' }
];
