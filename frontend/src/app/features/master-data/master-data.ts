import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

@Component({selector:'app-master-data',standalone:true,imports:[CommonModule,FormsModule],templateUrl:'./master-data.html',styleUrl:'./master-data.scss'})
export class MasterData implements OnInit {
  data=signal<any>({settings:[],aiProviders:[],languages:[],agents:[],extensions:[],queues:[],queueMembers:[],trunks:[],dids:[],asteriskNodes:[]});
  message=signal(''); error=signal(''); preview=signal('');
  setting:any={id:'00000000-0000-0000-0000-000000000000',category:'Telephony',key:'',value:'',valueType:'string',description:'',isSecret:false,isActive:true};
  provider:any={id:'00000000-0000-0000-0000-000000000000',name:'',providerType:'Ollama',capability:'Llm',baseUrl:'http://ollama:11434',model:'qwen3:4b',secretStoreReference:'',timeoutSeconds:30,priority:100,isDevelopment:true,isActive:true,optionsJson:'{}'};
  language:any={id:'00000000-0000-0000-0000-000000000000',code:'bn-BD',displayName:'বাংলা',speechRecognitionCode:'bn',voiceName:'default',welcomeMessage:'',fallbackMessage:'',priority:100,isDefault:false,isActive:true};
  agent:any={id:'00000000-0000-0000-0000-000000000000',userId:'00000000-0000-0000-0000-000000000000',displayName:'',extensionNumber:'',teamId:null,presence:1};
  queue:any={id:'00000000-0000-0000-0000-000000000000',name:'',strategy:'roundrobin',maxWaitSeconds:30};
  extension:any={id:'00000000-0000-0000-0000-000000000000',extensionNumber:'',agentId:'',secretStoreReference:'',isRegistered:false};
  member:any={id:'00000000-0000-0000-0000-000000000000',queueId:'',agentId:'',priority:100,penalty:0,isActive:true};
  constructor(private http:HttpClient){}
  ngOnInit(){void this.load()}
  async load(){try{this.data.set(await firstValueFrom(this.http.get<any>(`${environment.apiBaseUrl}/master-data`)));this.error.set('')}catch(e:any){this.error.set(e?.error?.message??'Could not load master data. Run the master-data database migration first.')}}
  async save(path:string,item:any){try{await firstValueFrom(this.http.put(`${environment.apiBaseUrl}/master-data/${path}/${item.id}`,item));this.message.set('Saved. Services will use the updated active configuration.');await this.load()}catch(e:any){this.error.set(e?.error?.message??'Could not save.')}}
  async showPreview(){this.preview.set(await firstValueFrom(this.http.get(`${environment.apiBaseUrl}/master-data/asterisk/preview`,{responseType:'text'})))}
  label(list:any[],id:string,key='name'){return list.find(x=>x.id===id)?.[key]??id}
}
