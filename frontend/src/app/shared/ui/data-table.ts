import { ScrollingModule } from '@angular/cdk/scrolling';
import { Component, input } from '@angular/core';

export type TableDensity = 'compact' | 'default' | 'comfortable';
export type TableState = 'live' | 'booked' | 'transferred' | 'abandoned' | 'suppressed' | 'machine' | 'queued';

export interface TableColumn {
  key: string;
  label: string;
  numeric?: boolean;
  mono?: boolean;
}

@Component({
  selector: 'ui-data-table',
  standalone: true,
  imports: [ScrollingModule],
  host: {
    '[class.density-compact]': "density() === 'compact'",
    '[class.density-comfortable]': "density() === 'comfortable'"
  },
  template: `
    <div class="data-table-wrap">
      @if (state() === 'loading') {
        <div class="ui-state" role="status" aria-busy="true"><div>Loading data...</div></div>
      } @else if (state() === 'error') {
        <div class="ui-state" role="alert"><div class="ui-state__title">Unable to load data</div><div>{{ message() }}</div></div>
      } @else if (state() === 'no-permission') {
        <div class="ui-state" role="status"><div class="ui-state__title">Access unavailable</div><div>{{ message() }}</div></div>
      } @else if (rows().length === 0) {
        <div class="ui-state" role="status"><div class="ui-state__title">{{ emptyTitle() }}</div><div>{{ message() }}</div></div>
      } @else {
        <cdk-virtual-scroll-viewport class="data-table-viewport" [itemSize]="rowHeight()">
          <table class="data-table" [class.data-table__sticky-first]="stickyFirst()">
            <caption class="sr-only">{{ caption() }}</caption>
            <thead><tr>
              @for (column of columns(); track column.key) {
                <th scope="col" [class.numeric]="column.numeric">{{ column.label }}</th>
              }
            </tr></thead>
            <tbody>
              <tr *cdkVirtualFor="let row of rows(); trackBy: trackRow" [class]="rowClass(row)" [attr.aria-selected]="rowSelected(row)">
                @for (column of columns(); track column.key) {
                  <td [class.numeric]="column.numeric" [class.mono]="column.mono">
                    <span class="data-table__value" tabindex="0" [title]="value(row, column)">{{ value(row, column) }}</span>
                  </td>
                }
              </tr>
            </tbody>
          </table>
        </cdk-virtual-scroll-viewport>
      }
    </div>
  `
})
export class UiDataTable {
  readonly rows = input<ReadonlyArray<Record<string, unknown>>>([]);
  readonly columns = input.required<ReadonlyArray<TableColumn>>();
  readonly density = input<TableDensity>('default');
  readonly caption = input('Data table');
  readonly emptyTitle = input('No data yet');
  readonly message = input('');
  readonly state = input<'ready' | 'loading' | 'error' | 'no-permission'>('ready');
  readonly stickyFirst = input(false);
  readonly rowState = input<(row: Record<string, unknown>) => TableState | undefined>();
  readonly rowKey = input<(row: Record<string, unknown>) => string | number>((row) => String(row['id'] ?? ''));
  readonly selectedRow = input<string | number | null>(null);

  rowHeight(): number {
    return this.density() === 'compact' ? 28 : this.density() === 'comfortable' ? 44 : 36;
  }

  trackRow = (_: number, row: Record<string, unknown>): string | number => this.rowKey()(row);

  value(row: Record<string, unknown>, column: TableColumn): string {
    return String(row[column.key] ?? '');
  }

  rowClass(row: Record<string, unknown>): string {
    const state = this.rowState()?.(row);
    return state ? `state-row--${state}` : '';
  }

  rowSelected(row: Record<string, unknown>): boolean | null {
    const selected = this.selectedRow();
    return selected === null ? null : this.rowKey()(row) === selected;
  }
}