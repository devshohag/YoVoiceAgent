import { Component, input, model } from '@angular/core';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
export type BadgeState = 'live' | 'booked' | 'transferred' | 'abandoned' | 'suppressed' | 'neutral' | 'pending';
export type StateKind = 'loading' | 'empty' | 'error' | 'no-permission';

@Component({
  selector: 'ui-button',
  standalone: true,
  template: '<button class="ui-button" [class]="\'ui-button ui-button--\' + variant()" [type]="type()" [disabled]="disabled()"><ng-content /></button>'
})
export class UiButton {
  readonly variant = input<ButtonVariant>('secondary');
  readonly type = input<'button' | 'submit' | 'reset'>('button');
  readonly disabled = input(false);
}

@Component({
  selector: 'ui-icon-button',
  standalone: true,
  template: '<button class="ui-icon-button" [type]="type()" [disabled]="disabled()" [attr.aria-label]="label()" [attr.title]="label()"><ng-content /></button>'
})
export class UiIconButton {
  readonly label = input.required<string>();
  readonly type = input<'button' | 'submit' | 'reset'>('button');
  readonly disabled = input(false);
}

@Component({
  selector: 'ui-badge',
  standalone: true,
  template: '<span class="ui-badge" [class]="\'ui-badge ui-badge--\' + state()" [attr.aria-label]="label()"><span class="ui-badge__dot" aria-hidden="true"></span><span>{{ label() }}</span></span>'
})
export class UiBadge {
  readonly state = input.required<BadgeState>();
  readonly label = input.required<string>();
}

@Component({
  selector: 'ui-state',
  standalone: true,
  template: '<section class="ui-state" [attr.aria-busy]="kind() === \'loading\'" role="status"><div><div class="ui-state__title">{{ title() }}</div><div>{{ message() }}</div><ng-content /></div></section>'
})
export class UiState {
  readonly kind = input.required<StateKind>();
  readonly title = input.required<string>();
  readonly message = input('');
}

@Component({
  selector: 'ui-input',
  standalone: true,
  template: '<input class="ui-input" [value]="value()" [type]="type()" [placeholder]="placeholder()" [disabled]="disabled()" [attr.aria-invalid]="invalid()" (input)="value.set(inputValue($event))" />'
})
export class UiInput {
  readonly value = model('');
  readonly type = input('text');
  readonly placeholder = input('');
  readonly disabled = input(false);
  readonly invalid = input(false);

  inputValue(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }
}

@Component({
  selector: 'ui-select',
  standalone: true,
  template: '<select class="ui-select" [value]="value()" [disabled]="disabled()" [attr.aria-invalid]="invalid()" (change)="value.set(selectValue($event))"><ng-content /></select>'
})
export class UiSelect {
  readonly value = model('');
  readonly disabled = input(false);
  readonly invalid = input(false);

  selectValue(event: Event): string {
    return (event.target as HTMLSelectElement).value;
  }
}

@Component({
  selector: 'ui-field',
  standalone: true,
  template: '<label class="ui-field"><span class="ui-field__label">{{ label() }}</span><ng-content /><span class="ui-field__error" [id]="errorId()" [hidden]="!error()">{{ error() }}</span></label>'
})
export class UiField {
  readonly label = input.required<string>();
  readonly error = input('');
  readonly errorId = input('field-error');
}

@Component({
  selector: 'ui-card',
  standalone: true,
  template: '<article class="ui-card"><ng-content /></article>'
})
export class UiCard {}

@Component({
  selector: 'ui-avatar',
  standalone: true,
  template: '<span class="ui-avatar" [attr.aria-label]="name()" [attr.title]="name()">{{ initials() }}</span>'
})
export class UiAvatar {
  readonly name = input.required<string>();

  initials(): string {
    return this.name().trim().split(/\s+/).map(part => part[0]).join('').slice(0, 2).toUpperCase();
  }
}

@Component({
  selector: 'ui-tag',
  standalone: true,
  template: '<span class="ui-tag"><ng-content /></span>'
})
export class UiTag {}

@Component({
  selector: 'ui-tabs',
  standalone: true,
  template: '<div class="ui-tabs" role="tablist"><ng-content /></div>'
})
export class UiTabs {}

@Component({
  selector: 'ui-tooltip',
  standalone: true,
  template: '<span class="ui-tooltip"><ng-content select="[uiTooltipTrigger]" /><span class="ui-tooltip__content" role="tooltip"><ng-content select="[uiTooltipContent]" /></span></span>'
})
export class UiTooltip {}