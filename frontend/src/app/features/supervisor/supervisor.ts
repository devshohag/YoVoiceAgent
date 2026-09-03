import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, OnInit, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

@Component({ selector: 'app-supervisor', standalone: true, imports: [DatePipe], templateUrl: './supervisor.html', styleUrl: './supervisor.scss' })
export class Supervisor implements OnInit {
  calls = signal<any[]>([]); selected = signal<any>(null); overview = signal<any>(null); error = signal('');
  constructor(private http: HttpClient) {}
  ngOnInit(): void { void this.load(); }
  async load(): Promise<void> {
    try {
      const [calls, overview] = await Promise.all([
        firstValueFrom(this.http.get<any[]>(`${environment.apiBaseUrl}/calls?take=100`)),
        firstValueFrom(this.http.get<any>(`${environment.apiBaseUrl}/calls/monitoring/overview`))
      ]);
      this.calls.set(calls); this.overview.set(overview); this.error.set('');
    } catch (e: any) { this.error.set(e?.error?.message ?? 'Could not load calls.'); }
  }
  async open(id: string): Promise<void> {
    const detail = await firstValueFrom(this.http.get<any>(`${environment.apiBaseUrl}/calls/${id}`));
    detail.recordings = await Promise.all(detail.recordings.map(async (recording: any) => {
      const blob = await firstValueFrom(this.http.get(`${environment.apiBaseUrl}/calls/recordings/${recording.id}`, { responseType: 'blob' }));
      return { ...recording, url: URL.createObjectURL(blob) };
    }));
    this.selected.set(detail);
  }
}
