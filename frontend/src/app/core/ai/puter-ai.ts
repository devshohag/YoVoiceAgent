import { Injectable } from '@angular/core';

declare const puter: any;

export interface PuterChatMessage {
  role: 'system' | 'assistant' | 'user';
  content: string;
}

export interface PuterToolDefinition {
  type: 'function';
  function: { name: string; description: string; parameters: Record<string, unknown> };
}

export interface ExecutedToolResult { name: string; argumentsJson: string; resultJson: string; }

@Injectable({ providedIn: 'root' })
export class PuterAi {
  private get sdk(): any {
    if (typeof puter === 'undefined') {
      throw new Error('Puter.js did not load. Check your internet connection or content blocker.');
    }
    return puter;
  }

  async chat(messages: PuterChatMessage[]): Promise<string> {
    const response = await this.sdk.ai.chat(messages, {
      model: 'gpt-5-nano',
      temperature: 0.3,
      reasoning_effort: 'minimal',
      verbosity: 'low'
    });
    const reply = this.extractText(response?.message?.content)
      || this.extractText(response?.message)
      || this.extractText(response?.content)
      || this.extractText(response);

    if (!reply) {
      console.error('Puter returned a response without readable text:', response);
      throw new Error('Puter returned an empty response. Open the browser console for details.');
    }
    return reply;
  }

  async chatWithTools(
    messages: PuterChatMessage[],
    tools: PuterToolDefinition[],
    executeTool: (name: string, argumentsJson: string) => Promise<string>,
    onExecuted?: (result: ExecutedToolResult) => void
  ): Promise<string> {
    const history: any[] = [...messages];

    for (let round = 0; round < 4; round++) {
      const response = await this.sdk.ai.chat(history, {
        model: 'gpt-5-nano', temperature: 0.2,
        reasoning_effort: 'minimal', verbosity: 'low', tools
      });
      const calls = response?.message?.tool_calls ?? [];
      if (!calls.length) {
        const reply = this.extractText(response?.message?.content)
          || this.extractText(response?.message) || this.extractText(response);
        if (!reply) throw new Error('Puter returned an empty response after tool processing.');
        return reply;
      }

      history.push(response.message);
      for (const call of calls) {
        const name = call?.function?.name;
        const rawArguments = call?.function?.arguments ?? '{}';
        const argumentsJson = typeof rawArguments === 'string'
          ? rawArguments : JSON.stringify(rawArguments);
        const resultJson = await executeTool(name, argumentsJson);
        onExecuted?.({ name, argumentsJson, resultJson });
        history.push({ role: 'tool', tool_call_id: call.id, content: resultJson });
      }
    }
    throw new Error('The AI exceeded the maximum number of tool-call rounds.');
  }

  /** Puter normalizes several providers, but their content blocks are not all identical. */
  private extractText(value: any, visited = new Set<any>()): string {
    if (value == null) return '';
    if (typeof value === 'string') return value.trim();
    if (typeof value === 'number' || typeof value === 'boolean') return '';
    if (typeof value !== 'object' || visited.has(value)) return '';
    visited.add(value);

    if (Array.isArray(value)) {
      return value.map(item => this.extractText(item, visited)).filter(Boolean).join('\n').trim();
    }

    // Known OpenAI/Anthropic/Puter response-block properties, in priority order.
    for (const key of ['text', 'output_text', 'value', 'content', 'parts', 'output']) {
      const text = this.extractText(value[key], visited);
      if (text) return text;
    }

    // ChatResponse objects can expose their answer through a custom toString().
    const rendered = String(value);
    return rendered !== '[object Object]' ? rendered.trim() : '';
  }

  async createSpeech(text: string, language: string): Promise<HTMLAudioElement> {
    return await this.sdk.ai.txt2speech(text, {
      provider: 'openai', model: 'gpt-4o-mini-tts', voice: 'alloy', response_format: 'wav',
      instructions: `Speak naturally in ${language || 'the language used in the text'} with a calm customer-support tone.`
    });
  }

  async transcribe(file: File, language: string): Promise<string> {
    const options: any = {
      provider: 'openai', model: 'gpt-4o-mini-transcribe',
      response_format: 'json'
    };
    if (language && language.toLowerCase() !== 'auto') options.language = language.split('-')[0];
    const result = await this.sdk.ai.speech2txt(file, options);
    const transcript = result?.text ?? result?.transcript ?? result?.data?.text;
    if (!transcript) {
      console.error('Puter transcription response:', result);
      throw new Error('Puter returned no transcript for this audio file.');
    }
    return String(transcript).trim();
  }
}
