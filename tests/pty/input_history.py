"""Real CLI PTY regression. Protocol bytes are injected, not physical keys.
Run after Release build; only temporary homes and fake credentials are used.
"""
import os,pty,fcntl,struct,subprocess,select,time,tempfile,json,shlex,stat
from pathlib import Path
repo=Path(__file__).resolve().parents[2];cli=repo/'src/Auxim.Cli/bin/Release/net10.0/auxim.dll'
root=Path(tempfile.mkdtemp(prefix='auxim-input-history-'));results=[]
class Cli:
 def __init__(self,home,mode='inline',provider='local'):
  self.home=home;self.data=bytearray();self.mode=mode
  self.m,self.s=pty.openpty();fcntl.ioctl(self.s,termios.TIOCSWINSZ,struct.pack('HHHH',24,32 if mode=='narrow' else 80,0,0));self.before=termios.tcgetattr(self.s)
  env=dict(os.environ,AUXIM_HOME=str(home),AUXIM_PROVIDER=provider,TERM='dumb' if mode=='dumb' else 'xterm-256color',COLORTERM='truecolor')
  env.pop('AUXIM_NO_ALT_SCREEN',None);env.pop('NO_COLOR',None)
  if mode in ('inline','dumb'):env['AUXIM_NO_ALT_SCREEN']='1'
  if mode=='no-color':env['NO_COLOR']='1'
  self.p=subprocess.Popen(['dotnet',str(cli)],stdin=self.s,stdout=self.s,stderr=self.s,env=env,start_new_session=True,cwd=repo)
  self.wait('auxim' if mode in ('inline','dumb') else 'Message Auxim...');self.drain(.3)
 def drain(self,t=.15):
  end=time.monotonic()+t
  while time.monotonic()<end:
   if select.select([self.m],[],[],.01)[0]:
    try:
     b=os.read(self.m,65536);self.data.extend(b)
     if b'\x1b[6n' in b:os.write(self.m,b'\x1b[20;8R')
    except OSError:pass
 def wait(self,text,at=0):
  end=time.monotonic()+8
  while text.encode() not in self.data[at:]:
   if time.monotonic()>end:raise AssertionError((self.mode,text,bytes(self.data[-700:])))
   self.drain(.03)
  self.drain()
 def send(self,text):os.write(self.m,text.encode());self.drain()
 def paste(self,text):self.send('\x1b[200~'+text+'\x1b[201~')
 def messages(self):
  pointer=self.home/'current_session'
  if not pointer.exists():return []
  f=self.home/'sessions'/(pointer.read_text().strip()+'.json')
  return json.loads(f.read_text())['messages'] if f.exists() else []
 def finish(self,text):
  before=len(self.messages());at=len(self.data);self.send('\r');self.wait('done in',at)
  assert len(self.messages())==before+2 and self.messages()[-2]['content']==text,(text,self.messages())
 def records(self):return [json.loads(x) for x in (self.home/'history.cli-v1.jsonl').read_text().splitlines()]
 def recall(self,text):
  records=self.records();index=max(i for i,r in enumerate(records) if r['text']==text)
  self.send('\x10'*(len(records)-index))
 def close(self):
  self.send('/exit\r');self.p.wait(timeout=5);self.drain();after=termios.tcgetattr(self.s)
  self.before[3]&=~termios.PENDIN;after[3]&=~termios.PENDIN;assert self.before==after
  os.close(self.m);os.close(self.s)
 def abort(self):
  if self.p.poll() is None:self.p.kill();self.p.wait()
  try:os.close(self.m);os.close(self.s)
  except OSError:pass
import termios
for mode in ('inline','alternate','narrow','no-color','dumb'):
 home=root/mode;app=Cli(home,mode);target=home/'must-not-execute';host='//printf executed > '+shlex.quote(str(target))
 try:
  app.send('/paste\r');app.wait('single .');app.send(host+'\r');app.finish_text=None
  at=len(app.data);app.send('.\r');app.wait('done in',at);assert app.messages()[-2]['content']==host and not target.exists()
  pointer=(home/'current_session').read_text();session_count=len(list((home/'sessions').glob('*.json')))
  app.send('/paste\r');app.send('/new\r');at=len(app.data);app.send('.\r');app.wait('done in',at)
  assert app.messages()[-2]['content']=='/new' and (home/'current_session').read_text()==pointer
  assert len(list((home/'sessions').glob('*.json')))==session_count
  body=host+'\r\n.\n/new\n中文🙂é'
  app.send('/paste\r');app.paste(body);app.send('\r');at=len(app.data);app.send('.\r');app.wait('done in',at)
  assert app.messages()[-2]['content']==body and not target.exists()
  app.paste(body);app.finish(body)
  app.send(host+'\\\r');at=len(app.data);app.send('continuation\r');app.wait('done in',at)
  assert app.messages()[-2]['content']==host+'\ncontinuation' and not target.exists()
  at=len(app.data);app.send('/new\\\r');app.send('continued message\r');app.wait('done in',at)
  assert app.messages()[-2]['content']=='/new\ncontinued message' and (home/'current_session').read_text()==pointer
  app.send('/status\r');app.drain(.3);app.send('\x1b');app.close()
  app=Cli(home,mode);app.recall(body);app.send(' edited');app.finish(body+' edited');assert not target.exists()
  app.recall('/status');before=len(app.messages());app.send('\r');app.drain(.4);assert len(app.messages())==before;app.send('\x1b')
  # Clearing a recalled message explicitly permits a fresh typed command.
  app.recall(body);app.send('\x01\x7f');app.send('/status\r');app.drain(.3);assert len(app.messages())==before;app.send('\x1b');app.close()
  assert stat.S_IMODE((home/'history.cli-v1.jsonl').stat().st_mode)==0o600
  results.append(dict(mode=mode,paste_safe=True,restart_exact=True,command_edit=True,private_history=True));print('PASS',results[-1],flush=True)
 finally:app.abort()
# Real credential command (fake value), no model request is made.
home=root/'credentials';app=Cli(home,provider='openai-compatible');fake='AUDIT_FAKE_CREDENTIAL_NOT_REAL'
try:
 at=len(app.data);app.send('/auth set-api-key '+fake+'\r');app.wait('saved to',at);app.drain(.2);app.send('/exit\r');app.p.wait(timeout=5)
 assert fake not in (home/'history.cli-v1.jsonl').read_text();assert not (home/'history').exists()
 assert fake not in '\n'.join(r['text'] for r in app.records());results.append(dict(credentials_filtered=True));print('PASS credential history filter',flush=True)
finally:app.abort()
# Preserve old bytes, recall ambiguous legacy command as a message, tighten mode.
home=root/'legacy';home.mkdir();old=home/'history';target=home/'legacy-executed';legacy='//printf old > '+shlex.quote(str(target));old_bytes=(legacy+'\n/auth set-api-key '+fake+'\n').encode();old.write_bytes(old_bytes);old.chmod(0o644)
app=Cli(home)
try:
 app.send('\x10');app.finish(legacy);assert not target.exists();app.close()
 assert old.read_bytes()==old_bytes and stat.S_IMODE(old.stat().st_mode)==0o600
 assert fake not in (home/'history.cli-v1.jsonl').read_text();results.append(dict(legacy_preserved=True));print('PASS legacy preservation and safe recall',flush=True)
finally:app.abort()
for wire in ('//printf damaged-history-must-be-message', '{"version":99,"text":"unknown"}'):
 home=root/('damaged-'+str(len(results)));home.mkdir();path=home/'history.cli-v1.jsonl';path.write_text(wire);app=Cli(home)
 try:
  app.send('\x10');app.finish(wire);app.close();assert path.read_text()==wire
  results.append(dict(damaged_preserved=True));print('PASS damaged/unknown history preserved',flush=True)
 finally:app.abort()
(root/'results.json').write_text(json.dumps(results,indent=2));print('Artifacts:',root)
