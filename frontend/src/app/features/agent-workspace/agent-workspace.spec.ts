import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AgentWorkspace } from './agent-workspace';
import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';

describe('AgentWorkspace', () => {
  let component: AgentWorkspace;
  let fixture: ComponentFixture<AgentWorkspace>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AgentWorkspace],
      providers: [{
        provide: HttpClient,
        useValue: {
          get: () => of([]),
          post: () => of({}),
          put: () => of({}),
        }
      }],
    }).compileComponents();

    fixture = TestBed.createComponent(AgentWorkspace);
    component = fixture.componentInstance;
    await fixture.whenStable();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  afterEach(() => fixture.destroy());
});
