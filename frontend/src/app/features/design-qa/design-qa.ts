import { DOCUMENT } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import {
  BadgeState, UiAvatar, UiBadge, UiButton, UiCard, UiField, UiIconButton,
  UiInput, UiSelect, UiState, UiTag, UiTabs, UiTooltip
} from '../../shared/ui/core-components';
import { TableColumn, TableState, UiDataTable } from '../../shared/ui/data-table';
import { UiLatencyChart, UiStageBreakdown } from '../../shared/ui/charts';

@Component({
  selector: 'app-design-qa',
  standalone: true,
  imports: [
    UiAvatar, UiBadge, UiButton, UiCard, UiDataTable, UiField, UiIconButton,
    UiInput, UiLatencyChart, UiSelect, UiStageBreakdown, UiState, UiTag, UiTooltip
  ],
  templateUrl: './design-qa.html',
  styleUrl: './design-qa.scss'
})
export class DesignQa {
  private readonly document = inject(DOCUMENT);
  readonly density = signal<'compact' | 'default' | 'comfortable'>('default');
  readonly theme = signal<'light' | 'dark'>(this.initialTheme());
  readonly inputValue = signal('');
  readonly columns: ReadonlyArray<TableColumn> = [
    { key: 'id', label: 'Call ID', mono: true },
    { key: 'caller', label: 'Caller' },
    { key: 'status', label: 'Status' },
    { key: 'duration', label: 'Duration', numeric: true, mono: true }
  ];
  readonly rows = Array.from({ length: 10000 }, (_, index) => ({
    id: `CALL-${String(index + 1).padStart(5, '0')}`,
    caller: index % 2 ? 'Rahim Uddin' : 'রহিম উদ্দিন',
    status: this.statuses[index % this.statuses.length],
    duration: `${String(index % 60).padStart(2, '0')}:${String(index % 60).padStart(2, '0')}`
  }));
  readonly statuses: BadgeState[] = ['live', 'booked', 'transferred', 'abandoned', 'suppressed', 'neutral', 'pending'];
  readonly latencyPoints = [
    { label: '09:00', p50: 1.4, p95: 3.1, concurrency: 2 },
    { label: '12:00', p50: 1.7, p95: 3.8, concurrency: 4 },
    { label: '15:00', p50: 1.2, p95: 2.7, concurrency: 3 },
    { label: '18:00', p50: 1.9, p95: 4.2, concurrency: 5 }
  ];
  readonly stageTiming = {
    p50: { stt: 45, endpoint: 33, routing: 18, llm: 4, tts: 3, rtp: 2 },
    p95: { stt: 112, endpoint: 82, routing: 46, llm: 19, tts: 12, rtp: 8 }
  };

  setTheme(theme: 'light' | 'dark'): void {
    this.theme.set(theme);
    this.document.documentElement.dataset['theme'] = theme;
  }

  setDensity(density: 'compact' | 'default' | 'comfortable'): void {
    this.density.set(density);
  }

  stateFor(row: Record<string, unknown>): TableState {
    const state = row['status'] as BadgeState;
    return state === 'neutral' ? 'machine' : state === 'pending' ? 'queued' : state;
  }

  private initialTheme(): 'light' | 'dark' {
    return this.document.documentElement.dataset['theme'] === 'dark' ? 'dark' : 'light';
  }
}