import { HttpClient } from '@angular/common/http';
import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { environment } from '../../../environments/environment';

export interface AiAgent {
  id: string;
  name: string;
  language: string;
  voiceName: string;
  welcomeMessage: string;
  systemPrompt: string;
  isActive: boolean;
  allowInboundCalls?: boolean;
}

@Component({
  selector: 'app-ai-agents',
  standalone: true,
  imports: [FormsModule, RouterLink],
  templateUrl: './ai-agents.html',
  styleUrl: './ai-agents.scss'
})
export class AiAgents implements OnInit {
  readonly agents = signal<AiAgent[]>([]);
  readonly message = signal('');
  name = 'Appointment Agent';
  language = 'en-US';
  voiceName = 'default';
  welcomeMessage = 'Hello. How can I help you today?';
  systemPrompt = 'You are an appointment assistant. Use only approved tools and transfer uncertain requests to a human.';

  constructor(private readonly http: HttpClient) {}

  ngOnInit(): void { this.load(); }

  load(): void {
    this.http.get<AiAgent[]>(`${environment.apiBaseUrl}/ai-agents`).subscribe({
      next: agents => this.agents.set(agents),
      error: error => this.message.set(error.error?.message ?? 'Could not load AI agents.')
    });
  }

  create(): void {
    this.message.set('');
    this.http.post<AiAgent>(`${environment.apiBaseUrl}/ai-agents`, {
      name: this.name,
      language: this.language,
      voiceName: this.voiceName,
      welcomeMessage: this.welcomeMessage,
      systemPrompt: this.systemPrompt,
      humanQueueId: null,
      allowInboundCalls: true,
      allowOutboundCalls: false
    }).subscribe({
      next: () => { this.message.set('AI agent created.'); this.load(); },
      error: error => this.message.set(error.error?.message ?? 'Could not create AI agent.')
    });
  }
}
