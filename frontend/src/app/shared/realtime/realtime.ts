import { Component, Injectable, signal } from '@angular/core';

export type ConnectionState = 'connected' | 'reconnecting' | 'disconnected';

@Injectable({ providedIn: 'root' })
export class RealtimeState {
  readonly connection = signal<ConnectionState>('connected');
  readonly pendingNewCount = signal(0);
  readonly revertedMessage = signal<string | null>(null);
  readonly lastUpdateAt = signal<Date | null>(null);

  setConnection(state: ConnectionState): void {
    this.connection.set(state);
  }

  noteNewItems(count = 1, listAtTop = false): void {
    if (listAtTop) {
      this.pendingNewCount.set(0);
      return;
    }
    this.pendingNewCount.update(current => current + Math.max(0, count));
  }

  acceptNewItems(): void {
    this.pendingNewCount.set(0);
  }

  markUpdated(): void {
    this.lastUpdateAt.set(new Date());
  }

  showReverted(message: string): void {
    this.revertedMessage.set(message);
  }

  clearReverted(): void {
    this.revertedMessage.set(null);
  }
}

@Component({
  selector: 'ui-realtime-status',
  standalone: true,
  template: `
    @if (realtime.connection() !== 'connected') {
      <div class="ws-drop-banner" role="status" aria-live="polite">
        @if (realtime.connection() === 'reconnecting') { <span class="spinner" aria-hidden="true"></span> }
        <span>{{ realtime.connection() === 'reconnecting' ? 'Connection lost. Reconnecting...' : 'Live updates unavailable.' }}</span>
      </div>
    }
    @if (realtime.revertedMessage(); as message) {
      <div class="action-reverted-banner" role="status" aria-live="polite">{{ message }}</div>
    }
    @if (realtime.pendingNewCount() > 0) {
      <button class="new-items-pill new-items-pill--visible" type="button" (click)="realtime.acceptNewItems()">
        {{ realtime.pendingNewCount() }} new {{ realtime.pendingNewCount() === 1 ? 'call' : 'calls' }}
      </button>
    }
  `
})
export class UiRealtimeStatus {
  constructor(readonly realtime: RealtimeState) {}
}