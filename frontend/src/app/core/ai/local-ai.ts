import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { PuterChatMessage, PuterToolDefinition } from './puter-ai';

interface LocalChatResponse {
  content: string;
  toolCalls?: Array<{ function?: { name?: string; arguments?: unknown } }>;
}

@Injectable({ providedIn: 'root' })
export class LocalAi {
  constructor(private readonly http: HttpClient) {}

  async chatWithTools(
    initialMessages: PuterChatMessage[],
    tools: PuterToolDefinition[],
    executeTool: (name: string, argumentsJson: string) => Promise<string>,
    onToolResult?: (result: { name: string; argumentsJson: string; resultJson: string }) => void
  ): Promise<string> {
    const messages: any[] = initialMessages.map(message => ({ ...message }));
    for (let round = 0; round < 6; round++) {
      const response = await firstValueFrom(this.http.post<LocalChatResponse>(
        `${environment.apiBaseUrl}/local-ai/chat`, { messages, tools }
      ));
      const calls = response.toolCalls ?? [];
      if (!calls.length) {
        const reply = (response.content ?? '').trim();
        if (!reply) throw new Error('Local AI returned an empty response.');
        return reply;
      }

      messages.push({ role: 'assistant', content: response.content ?? '', tool_calls: calls });
      for (const call of calls) {
        const name = call.function?.name ?? '';
        if (!name) throw new Error('Local AI requested an unnamed tool.');
        const rawArguments = call.function?.arguments;
        const argumentsJson = typeof rawArguments === 'string'
          ? rawArguments : JSON.stringify(rawArguments ?? {});
        const resultJson = await executeTool(name, argumentsJson);
        onToolResult?.({ name, argumentsJson, resultJson });
        messages.push({ role: 'tool', content: resultJson });
      }
    }
    throw new Error('Local AI exceeded the tool-processing limit.');
  }

  async transcribe(file: File, language = 'auto'): Promise<string> {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('language', language || 'auto');
    const result = await firstValueFrom(this.http.post<any>(
      `${environment.apiBaseUrl}/local-ai/transcriptions`, form
    ));
    const text = String(result?.text ?? result?.transcript ?? '').trim();
    if (!text) throw new Error('Local speech service returned no transcript.');
    return text;
  }

  async createSpeech(text: string, language = 'bn-BD'): Promise<HTMLAudioElement> {
    const blob = await firstValueFrom(this.http.post(
      `${environment.apiBaseUrl}/local-ai/speech`, { input: text, language }, { responseType: 'blob' }
    ));
    const url = URL.createObjectURL(blob);
    const audio = new Audio(url);
    audio.addEventListener('ended', () => URL.revokeObjectURL(url), { once: true });
    return audio;
  }
}
