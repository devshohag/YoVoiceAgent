import { HttpClient } from '@angular/common/http';
import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { PuterAi, PuterChatMessage, PuterToolDefinition } from '../../core/ai/puter-ai';
import { AiAgent } from '../ai-agents/ai-agents';

interface PendingTurn { callSessionId: string; recordingId: string; fromNumber: string; toNumber: string; durationSeconds: number; recordedAtUtc: string; }
interface BridgeCard { callSessionId: string; fromNumber: string; status: string; transcript?: string; reply?: string; error?: string; }
interface CallMemory { conversationId: string; messages: PuterChatMessage[]; tools: string[]; bookingAuthorized: boolean; completed: boolean; handoffRequested: boolean; handoffReason: string; }

const TOOLS: PuterToolDefinition[] = [
  { type: 'function', function: { name: 'check_appointment_availability', description: 'Check SQL appointment slots for a date before offering times.', parameters: { type: 'object', properties: { date: { type: 'string', description: 'YYYY-MM-DD' } }, required: ['date'] } } },
  { type: 'function', function: { name: 'book_appointment', description: 'Book only after explicit customer confirmation. Use the exact slotId from availability.', parameters: { type: 'object', properties: { slotId: { type: 'string' }, customerName: { type: 'string' }, contact: { type: 'string' }, purpose: { type: 'string' } }, required: ['slotId', 'customerName', 'contact'] } } },
  { type: 'function', function: { name: 'request_human_handoff', description: 'Request a human when the caller asks or the AI is uncertain.', parameters: { type: 'object', properties: { reason: { type: 'string' } }, required: ['reason'] } } }
];

@Component({
  selector: 'app-voice-bridge', standalone: true, imports: [FormsModule],
  templateUrl: './voice-bridge.html', styleUrl: './voice-bridge.scss'
})
export class VoiceBridge implements OnInit, OnDestroy {
  readonly agents = signal<AiAgent[]>([]);
  readonly cards = signal<BridgeCard[]>([]);
  readonly running = signal(false);
  readonly busy = signal(false);
  readonly status = signal('Bridge stopped');
  readonly error = signal('');
  readonly processingMode = signal('Loading');
  readonly maxConcurrentCalls = signal(1);
  selectedAgentId = '';
  private timer?: number;
  private readonly handled = new Set<string>();
  private readonly memory = new Map<string, CallMemory>();
  private readonly transcriptionFailures = new Map<string, number>();

  constructor(private readonly http: HttpClient, private readonly puter: PuterAi) {}

  async ngOnInit(): Promise<void> {
    try {
      const agents = await firstValueFrom(this.http.get<AiAgent[]>(`${environment.apiBaseUrl}/ai-agents`));
      this.agents.set(agents.filter(x => x.isActive && x.allowInboundCalls !== false));
      this.selectedAgentId = this.agents()[0]?.id ?? '';
      const mode: any = await firstValueFrom(this.http.get(`${environment.apiBaseUrl}/voice-bridge/mode`));
      this.processingMode.set(mode.mode ?? 'Puter');
      this.maxConcurrentCalls.set(mode.maxConcurrentCalls ?? 1);
      if (this.processingMode().toLowerCase() === 'local')
        this.status.set(`Local AI backend active — up to ${this.maxConcurrentCalls()} simultaneous calls`);
    } catch { this.error.set('Could not load AI agents. Log in again.'); }
  }

  ngOnDestroy(): void { this.stop(); }

  start(): void {
    if (this.processingMode().toLowerCase() === 'local') return;
    if (!this.selectedAgentId || this.running()) return;
    this.running.set(true); this.status.set('Listening for Asterisk recordings…');
    void this.poll();
    this.timer = window.setInterval(() => void this.poll(), 1200);
  }

  stop(): void {
    this.running.set(false); this.status.set('Bridge stopped');
    if (this.timer) window.clearInterval(this.timer);
    this.timer = undefined;
  }

  private async poll(): Promise<void> {
    if (!this.running() || this.busy()) return;
    try {
      const pending = await firstValueFrom(this.http.get<PendingTurn[]>(`${environment.apiBaseUrl}/voice-bridge/pending`));
      const next = pending.find(x => !this.handled.has(x.recordingId));
      if (next) await this.process(next);
      else this.status.set('Bridge ready — waiting for caller speech');
    } catch (error: any) { this.error.set(error?.error?.message ?? 'Voice bridge polling failed.'); }
  }

  private async process(turn: PendingTurn): Promise<void> {
    this.busy.set(true); this.handled.add(turn.recordingId); this.error.set('');
    this.upsert(turn, 'Downloading caller recording…');
    try {
      const agent = this.agents().find(x => x.id === this.selectedAgentId);
      if (!agent) throw new Error('Select an active AI agent.');
      const recording = await firstValueFrom(this.http.get(
        `${environment.apiBaseUrl}/voice-bridge/recordings/${turn.recordingId}`, { responseType: 'blob' }));
      const file = new File([recording], `caller-${turn.recordingId}.wav`, { type: 'audio/wav' });
      this.upsert(turn, 'Transcribing with Puter…');
      let transcript = '';
      try {
        transcript = String(await this.puter.transcribe(file, 'auto') ?? '').trim();
      } catch (error: any) {
        await this.recoverFromTranscriptionFailure(turn,
          error?.message ?? 'Puter returned no transcript for this audio file.');
        return;
      }
      if (!transcript) {
        await this.recoverFromTranscriptionFailure(turn,
          'Puter returned no transcript for this audio file.');
        return;
      }
      this.transcriptionFailures.delete(turn.callSessionId);
      const detectedLanguage = /[\u0980-\u09FF]/.test(transcript) ? 'bn-BD' : 'en-US';
      const memory = await this.getMemory(turn.callSessionId, agent);
      if (/\b(yes|confirm|confirmed|book|booking|go ahead|proceed)\b/i.test(transcript))
        memory.bookingAuthorized = true;
      memory.messages.push({ role: 'user', content: transcript });
      await this.saveTurn(memory.conversationId, 0, transcript);
      this.upsert(turn, 'Generating AI response…', transcript);

      // A live transfer must never depend only on the model choosing a tool. If the
      // caller explicitly asks for a person, execute the approved .NET handoff tool
      // deterministically and set the telephony flag before producing any speech.
      const explicitHumanRequest = /\b(?:human|operator|representative|supervisor|live\s+(?:person|agent)|real\s+(?:person|agent)|speak\s+(?:to|with)\s+(?:a\s+)?(?:person|human|agent)|transfer\s+(?:me|the\s+call))\b/i.test(transcript)
        || /(?:মানুষ|হিউম্যান|এজেন্ট|অপারেটর|প্রতিনিধি|সুপারভাইজার|কাস্টমার\s*কেয়ার|কথা\s*বলতে\s*চাই|কল\s*ট্রান্সফার)/i.test(transcript);
      if (explicitHumanRequest && !memory.handoffRequested) {
        await this.executeTool(memory, 'request_human_handoff', JSON.stringify({
          reason: `Caller explicitly requested a human agent: ${transcript.slice(0, 300)}`
        }), transcript);
      }

      const messages: PuterChatMessage[] = [
        { role: 'system', content: `${agent.systemPrompt}\nToday is ${new Date().toISOString().slice(0, 10)}. This is a live telephone call. Detect whether the caller uses Bangla or English and always reply in that same language. Reply with at most two short spoken sentences. Ask one question at a time. Use availability before offering slots. A Bangladesh mobile number must contain exactly 11 digits, start with 01, and have 3-9 as the third digit; if the booking tool rejects it, ask the caller to repeat the full number. Internally preserve exact slotIds for tool calls, but NEVER print or speak a slotId, bookingId, UUID/GUID, JSON, tool name, or voucher URL. Offer no more than three human-friendly times. Never claim booking success without book_appointment success. After a successful booking, state the date, time and booking reference, say thank you and goodbye, and ask no follow-up question. If the caller asks for a person, is angry, or the request is outside your approved tools, call request_human_handoff. After that tool, only say the configured hold message in the caller's language.` },
        ...(memory.tools.length ? [{ role: 'system' as const, content: `Approved tool results:\n${memory.tools.join('\n')}` }] : []),
        ...memory.messages
      ];
      const generatedReply = memory.handoffRequested ? '' : await this.puter.chatWithTools(messages, TOOLS,
        (name, args) => this.executeTool(memory, name, args, transcript),
        tool => { memory.tools.push(`${tool.name}: ${tool.resultJson}`); memory.tools = memory.tools.slice(-8); });
      const reply = memory.handoffRequested
        ? (detectedLanguage === 'bn-BD' ? 'অনুগ্রহ করে অপেক্ষা করুন, আমি আপনাকে একজন মানব সহায়তা প্রতিনিধির সাথে সংযুক্ত করছি।' : 'Please hold while I connect you to a human support agent.')
        : this.toCallerSafeReply(generatedReply, memory.completed);
      memory.messages.push({ role: 'assistant', content: reply });
      await this.saveTurn(memory.conversationId, 1, reply);
      this.upsert(turn, 'Creating telephony audio…', transcript, reply);

      const speech = await this.puter.createSpeech(reply, detectedLanguage);
      const wav = await this.toTelephonyWav(speech.src);
      const form = new FormData();
      form.append('audio', wav, `ai-${turn.recordingId}.wav`);
      form.append('recordingId', turn.recordingId);
      form.append('transcript', transcript);
      form.append('responseText', reply);
      form.append('endCall', String(memory.completed));
      form.append('handoffAfterPlayback', String(memory.handoffRequested));
      // The API resolves the next available queue member. No extension is hard-coded in the browser.
      form.append('handoffExtension', '');
      form.append('handoffReason', memory.handoffReason);
      form.append('conversationId', memory.conversationId);
      form.append('conversationJson', JSON.stringify(memory.messages.slice(-80)));
      await firstValueFrom(this.http.post(
        `${environment.apiBaseUrl}/voice-bridge/calls/${turn.callSessionId}/response`, form));
      this.upsert(turn, 'Response queued for Asterisk playback', transcript, reply);
      this.status.set('AI response sent to the caller');
    } catch (error: any) {
      const message = error?.message ?? error?.error?.message ?? 'Voice processing failed.';
      this.upsert(turn, 'Failed', undefined, undefined, message);
      this.error.set(String(message));
      try {
        await firstValueFrom(this.http.post(
          `${environment.apiBaseUrl}/voice-bridge/calls/${turn.callSessionId}/hangup`,
          { reason: String(message).slice(0, 500) }));
        this.status.set('Call ended safely after voice processing failed');
      } catch { /* Keep the original processing error visible. */ }
    } finally { this.busy.set(false); }
  }

  private async recoverFromTranscriptionFailure(turn: PendingTurn, reason: string): Promise<void> {
    const failures = (this.transcriptionFailures.get(turn.callSessionId) ?? 0) + 1;
    this.transcriptionFailures.set(turn.callSessionId, failures);

    // A single silent/unclear recording is normal on a telephone call. Keep the
    // channel alive, play a short retry prompt, and let Asterisk record a new turn.
    if (failures < 3) {
      const reply = failures === 1
        ? 'দুঃখিত, আমি কথাটি বুঝতে পারিনি। অনুগ্রহ করে আবার বলুন। Please say that again.'
        : 'আমি এখনও স্পষ্টভাবে শুনতে পারছি না। অনুগ্রহ করে একটু জোরে আবার বলুন। Please speak a little louder.';
      this.upsert(turn, `Speech not understood — retrying (${failures}/3)`, undefined, reply, reason);
      const speech = await this.puter.createSpeech(reply, 'bn-BD');
      const wav = await this.toTelephonyWav(speech.src);
      const form = new FormData();
      form.append('audio', wav, `retry-${turn.recordingId}.wav`);
      form.append('recordingId', turn.recordingId);
      form.append('transcript', '[No speech recognized]');
      form.append('responseText', reply);
      form.append('endCall', 'false');
      form.append('handoffAfterPlayback', 'false');
      form.append('handoffExtension', '');
      form.append('handoffReason', '');
      await firstValueFrom(this.http.post(
        `${environment.apiBaseUrl}/voice-bridge/calls/${turn.callSessionId}/response`, form));
      this.status.set('Caller was asked to repeat; the call remains connected');
      return;
    }

    // Avoid an endless silent loop. Only after three consecutive failed turns do
    // we end the development bridge call in a controlled and auditable way.
    await firstValueFrom(this.http.post(
      `${environment.apiBaseUrl}/voice-bridge/calls/${turn.callSessionId}/hangup`,
      { reason: `Speech recognition failed three consecutive times. Last error: ${reason}`.slice(0, 500) }));
    this.upsert(turn, 'Ended after 3 unsuccessful speech attempts', undefined, undefined, reason);
    this.status.set('Call ended after three consecutive speech-recognition failures');
  }

  private async getMemory(callSessionId: string, agent: AiAgent): Promise<CallMemory> {
    const existing = this.memory.get(callSessionId); if (existing) return existing;
    const conversation: any = await firstValueFrom(this.http.post(
      `${environment.apiBaseUrl}/ai-conversations/test`, { aiAgentId: agent.id }));
    const created: CallMemory = { conversationId: conversation.id, messages: [], tools: [], bookingAuthorized: false, completed: false, handoffRequested: false, handoffReason: '' };
    this.memory.set(callSessionId, created); return created;
  }

  private async executeTool(memory: CallMemory, name: string, argumentsJson: string, latest: string): Promise<string> {
    const confirmed = name !== 'book_appointment' || memory.bookingAuthorized
      || /\b(yes|confirm|book|go ahead|proceed)\b/i.test(latest);
    const execution: any = await firstValueFrom(this.http.post(
      `${environment.apiBaseUrl}/ai-conversations/${memory.conversationId}/tools`,
      { toolName: name, argumentsJson, confirmed }));
    if (name === 'book_appointment' && execution.resultJson) {
      memory.bookingAuthorized = false;
      try {
        const result = JSON.parse(execution.resultJson);
        memory.completed = result?.success === true && result?.status === 'Confirmed';
      } catch { memory.completed = false; }
    }
    if (name === 'request_human_handoff') {
      memory.handoffRequested = true;
      try { memory.handoffReason = String(JSON.parse(argumentsJson)?.reason ?? 'Caller requested a human agent').slice(0, 500); }
      catch { memory.handoffReason = 'Caller requested a human agent'; }
    }
    return execution.resultJson ?? JSON.stringify({ success: false, reason: execution.failureReason ?? 'Tool failed.' });
  }

  private async saveTurn(conversationId: string, speaker: number, text: string): Promise<void> {
    await firstValueFrom(this.http.post(
      `${environment.apiBaseUrl}/ai-conversations/${conversationId}/turns`, { speaker, text }));
  }

  private toCallerSafeReply(value: string, completed: boolean): string {
    let text = String(value ?? '').trim();

    // Tool identifiers remain in audited .NET results and call memory, but must never be
    // displayed or spoken to a telephone caller.
    text = text
      .replace(/\(?\s*(?:slot\s*id|slotId|bookingId)\s*[:=]?\s*[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\s*\)?/gi, '')
      .replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi, '')
      .replace(/\s+([,.;:!?])/g, '$1')
      .replace(/[ \t]{2,}/g, ' ')
      .trim();

    if (completed) {
      text = text
        .replace(/\s*[;,.]?\s*(?:would|do|can|may|shall)\s+you\b.*\?\s*$/i, '')
        .replace(/\s*\?\s*$/, '.')
        .trim();
      if (!/\b(?:thank you|goodbye)\b/i.test(text)) text = `${text} Thank you and goodbye.`;
    }

    // Keep telephone responses concise even if a development provider ignores the prompt.
    const sentences = text.match(/[^.!?]+[.!?]+|[^.!?]+$/g) ?? [];
    text = sentences.slice(0, 2).join(' ').trim();
    return text || 'I am sorry, I could not prepare a safe response. Please try again.';
  }

  private upsert(turn: PendingTurn, status: string, transcript?: string, reply?: string, error?: string): void {
    this.cards.update(items => {
      const card: BridgeCard = { callSessionId: turn.callSessionId, fromNumber: turn.fromNumber,
        status, transcript, reply, error };
      return [card, ...items.filter(x => x.callSessionId !== turn.callSessionId)].slice(0, 10);
    });
  }

  private async toTelephonyWav(sourceUrl: string): Promise<Blob> {
    const encoded = await (await fetch(sourceUrl)).arrayBuffer();
    const context = new AudioContext();
    const decoded = await context.decodeAudioData(encoded.slice(0));
    const frames = Math.ceil(decoded.duration * 8000);
    const offline = new OfflineAudioContext(1, frames, 8000);
    const node = offline.createBufferSource(); node.buffer = decoded; node.connect(offline.destination); node.start();
    const rendered = await offline.startRendering(); await context.close();
    return new Blob([this.encodeWav(rendered.getChannelData(0), 8000)], { type: 'audio/wav' });
  }

  private encodeWav(samples: Float32Array, sampleRate: number): ArrayBuffer {
    const buffer = new ArrayBuffer(44 + samples.length * 2); const view = new DataView(buffer);
    const text = (offset: number, value: string) => [...value].forEach((c, i) => view.setUint8(offset + i, c.charCodeAt(0)));
    text(0, 'RIFF'); view.setUint32(4, 36 + samples.length * 2, true); text(8, 'WAVE'); text(12, 'fmt ');
    view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
    view.setUint32(24, sampleRate, true); view.setUint32(28, sampleRate * 2, true);
    view.setUint16(32, 2, true); view.setUint16(34, 16, true); text(36, 'data');
    view.setUint32(40, samples.length * 2, true);
    for (let i = 0; i < samples.length; i++) {
      const sample = Math.max(-1, Math.min(1, samples[i]));
      view.setInt16(44 + i * 2, sample < 0 ? sample * 0x8000 : sample * 0x7fff, true);
    }
    return buffer;
  }
}
