import { Component, input } from '@angular/core';

export interface LatencyPoint {
  label: string;
  p50: number;
  p95: number;
  concurrency: number;
}

export interface StageTiming {
  stt: number;
  endpoint: number;
  routing: number;
  llm: number;
  tts: number;
  rtp: number;
}

export interface StageTimingSet {
  p50: StageTiming;
  p95: StageTiming;
}

@Component({
  selector: 'ui-latency-chart',
  standalone: true,
  template: `
    @if (state() === 'empty') {
      <div class="chart-empty-state"><span class="chart-empty-state__title">No calls yet</span><span>{{ message() }}</span></div>
    } @else if (state() === 'nodata') {
      <div class="chart-nodata-state"><span class="chart-nodata-state__title">Not enough data yet</span><span>{{ message() }}</span></div>
    } @else {
      <section class="chart-panel" [attr.aria-label]="title()">
        <h3 class="chart-panel__title">{{ title() }}</h3>
        <p class="chart-panel__subtitle">{{ subtitle() }}</p>
        <svg class="latency-chart" viewBox="0 0 640 220" role="img" [attr.aria-label]="title()">
          <line class="latency-chart__grid" x1="40" y1="20" x2="620" y2="20" />
          <line class="latency-chart__grid" x1="40" y1="110" x2="620" y2="110" />
          <line class="latency-chart__grid" x1="40" y1="200" x2="620" y2="200" />
          <text class="latency-chart__axis" x="4" y="24">{{ maxValue() }}s</text>
          <text class="latency-chart__axis" x="16" y="204">0s</text>
          <path class="latency-chart__concurrency" [attr.d]="concurrencyPath()" />
          <path class="latency-chart__line--p50" [attr.d]="linePath('p50')" />
          <path class="latency-chart__line--p95" [attr.d]="linePath('p95')" />
          @for (point of points(); track point.label; let index = $index) {
            <circle class="latency-chart__dot--p50" [attr.cx]="x(index)" [attr.cy]="y(point.p50)" r="3" />
            <circle class="latency-chart__dot--p95" [attr.cx]="x(index)" [attr.cy]="y(point.p95)" r="4" />
            <text class="latency-chart__axis" [attr.x]="x(index)" y="218" text-anchor="middle">{{ point.label }}</text>
          }
        </svg>
      </section>
    }
  `
})
export class UiLatencyChart {
  readonly points = input<ReadonlyArray<LatencyPoint>>([]);
  readonly title = input('p95 time-to-first-audio by stage');
  readonly subtitle = input('Percentiles, seconds; concurrency shown behind the lines');
  readonly state = input<'ready' | 'empty' | 'nodata'>('ready');
  readonly message = input('');

  maxValue(): number {
    return Math.max(1, ...this.points().map(point => point.p95));
  }

  x(index: number): number {
    return this.points().length < 2 ? 330 : 40 + index * 580 / (this.points().length - 1);
  }

  y(value: number): number {
    return 200 - value / this.maxValue() * 180;
  }

  linePath(metric: 'p50' | 'p95'): string {
    return this.points().map((point, index) => `${index === 0 ? 'M' : 'L'} ${this.x(index)} ${this.y(point[metric])}`).join(' ');
  }

  concurrencyPath(): string {
    const points = this.points();
    if (points.length === 0) return '';
    return `M ${this.x(0)} 200 `
      + points.map((point, index) => `L ${this.x(index)} ${200 - point.concurrency / Math.max(1, ...points.map(item => item.concurrency)) * 160}`).join(' ')
      + ` L ${this.x(points.length - 1)} 200 Z`;
  }
}

@Component({
  selector: 'ui-stage-breakdown',
  standalone: true,
  template: `
    <section class="chart-panel" [attr.aria-label]="title()">
      <h3 class="chart-panel__title">{{ title() }}</h3>
      <div class="stage-breakdown">
        @for (stage of stages; track stage.key) {
          <div class="stage-breakdown__row">
            <span class="stage-breakdown__label">{{ stage.label }}</span>
            <div class="stage-breakdown__bar" role="img" [attr.aria-label]="stage.label + ': ' + total(stage.key) + ' milliseconds'">
              @for (segment of segments(stage.key); track segment.key) {
                <span class="stage-breakdown__segment" [class]="'stage-breakdown__segment stage-breakdown__segment--' + segment.key" [style.width.%]="segment.value / total(stage.key) * 100">{{ segment.value >= 10 ? segment.value : '' }}</span>
              }
            </div>
          </div>
        }
      </div>
    </section>
  `
})
export class UiStageBreakdown {
  readonly title = input('Stage timing by percentile');
  readonly values = input<StageTimingSet>({
    p50: { stt: 0, endpoint: 0, routing: 0, llm: 0, tts: 0, rtp: 0 },
    p95: { stt: 0, endpoint: 0, routing: 0, llm: 0, tts: 0, rtp: 0 }
  });
  readonly stages = [{ key: 'p50', label: 'p50' }, { key: 'p95', label: 'p95' }];

  segments(stage: string): Array<{ key: keyof StageTiming; value: number }> {
    const values = this.values()[stage as keyof StageTimingSet];
    return [
      { key: 'stt', value: values.stt }, { key: 'endpoint', value: values.endpoint },
      { key: 'routing', value: values.routing }, { key: 'llm', value: values.llm },
      { key: 'tts', value: values.tts }, { key: 'rtp', value: values.rtp }
    ];
  }

  total(stage: string): number { return this.segments(stage).reduce((sum, segment) => sum + segment.value, 0) || 1; }
}