# PTY protocol injection, not a physical-key test. Run after Release build.
import subprocess
import re
import tempfile
import os,pty,termios,fcntl,struct,select,time,json,threading,http.server,signal,sys,uuid
from pathlib import Path
root=Path(tempfile.mkdtemp(prefix='auxim-kitty-protocol-'));repo=Path(__file__).resolve().parents[2];cli=str(repo/'src/Auxim.Cli/bin/Release/net10.0/auxim.dll');reports=[]
for mode,w in [('alternate',80),('alternate',32),('inline',80),('no-color',80),('dumb',80),('raw-key',80)]:
 if os.environ.get('ONLY') and mode not in os.environ['ONLY'].split(','):continue
 home=root/(mode+str(w)+'-'+uuid.uuid4().hex);slow=threading.Event();calls=[]
 class Server(http.server.ThreadingHTTPServer):daemon_threads=True
 class Handler(http.server.BaseHTTPRequestHandler):
  def log_message(self,*a):pass
  def do_GET(self):
   slow.set();time.sleep(3)
   try:self.send_response(200);self.end_headers();self.wfile.write(b'tool done')
   except OSError:pass
  def do_POST(self):
   body=json.loads(self.rfile.read(int(self.headers.get('Content-Length',0))));prompt=[m['content'] for m in body['messages'] if m['role']=='user'][-1];calls.append(prompt)
   try:
    if not body.get('stream',False) and prompt=='plain-output':
     self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers()
     self.wfile.write(json.dumps({'choices':[{'message':{'content':'plain output'}}]}).encode());return
    self.send_response(200);self.send_header('Content-Type','text/event-stream');self.end_headers()
    def delta(d):self.wfile.write(('data: '+json.dumps({'choices':[{'delta':d}]},ensure_ascii=False)+'\n\n').encode());self.wfile.flush()
    if prompt in ('stream','repeat','signal'):
     delta({'content':'partial-'+prompt+' 中文🙂\nkeep this preview'})
     for i in range(100):time.sleep(.1);delta({'content':'.'})
    elif prompt in ('approve','tool'):
     name='file.write' if prompt=='approve' else 'web.fetch';args={'path':'/tmp/should-not-exist','content':'bad'} if prompt=='approve' else {'url':'http://127.0.0.1:%d/slow'%server.server_port}
     delta({'tool_calls':[{'index':0,'id':'call','function':{'name':name,'arguments':json.dumps(args)}}]})
    else:delta({'content':'OK '+prompt})
    self.wfile.write(b'data: [DONE]\n\n');self.wfile.flush()
   except (OSError,ConnectionError):pass
 server=Server(('127.0.0.1',0),Handler)
 env=dict(os.environ,AUXIM_HOME=str(home),AUXIM_PROVIDER='openai-compatible',AUXIM_MODEL='test',AUXIM_BASE_URL='http://127.0.0.1:%d/v1'%server.server_port,AUXIM_API_KEY='fake',TERM='xterm-256color',COLORTERM='truecolor')
 env.pop('NO_COLOR',None);env.pop('AUXIM_NO_ALT_SCREEN',None)
 if mode=='dumb':env['TERM']='dumb'
 if mode=='inline':env['AUXIM_NO_ALT_SCREEN']='1'
 if mode=='no-color':env['NO_COLOR']='1'
 # A controlling PTY permits direct SIGINT tests alongside CSI-u injection.
 pid,m=pty.fork()
 if pid==0:
  if mode=='raw-key':
   raw=termios.tcgetattr(0);raw[3]&=~termios.ISIG;termios.tcsetattr(0,termios.TCSANOW,raw)
  os.chdir(repo);os.execvpe('dotnet',['dotnet',cli],env)
 threading.Thread(target=server.serve_forever,daemon=True).start();initial=None;data=bytearray();exitstatus=None
 fcntl.ioctl(m,termios.TIOCSWINSZ,struct.pack('HHHH',24,w,0,0));os.kill(pid,signal.SIGWINCH)
 flags={False:8,True:0};stacks={False:[],True:[]};screen=False;wire=b'';queries=0;child_checks=0
 def terminal_output(chunk):
  global screen,wire,queries,child_checks
  wire+=chunk;last=0
  for match in re.finditer(rb'\x1b\[[0-?]*[ -/]*[@-~]|protocol-foreground-ok',wire):
   code=match.group();last=match.end()
   if code==b'\x1b[?1049h':
    assert not stacks[False];screen=True
   elif code==b'\x1b[?1049l':
    assert not stacks[True];screen=False
   elif code==b'\x1b[?u':
    queries+=1;os.write(m,('\x1b[?%du'%flags[screen]).encode())
   elif code in (b'\x1b[>1u',b'\x1b[>0u'):
    stacks[screen].append(flags[screen]);flags[screen]=int(chr(code[-2]));assert len(stacks[screen])==1
   elif code==b'protocol-foreground-ok':
    assert not stacks[screen] and flags[screen]==(0 if screen else 8);child_checks+=1
   elif code==b'\x1b[<u':
    assert stacks[screen],'unowned keyboard pop';flags[screen]=stacks[screen].pop()
  tail=wire[last:];at=tail.rfind(b'\x1b');wire=tail[at:] if at>=0 else b''
 def drain(t=.12):
  end=time.monotonic()+t
  while time.monotonic()<end:
   if select.select([m],[],[],.01)[0]:
    try:
     b=os.read(m,65536);data.extend(b);terminal_output(b)
     if b'\x1b[6n' in b:os.write(m,b'\x1b[20;1R')
    except OSError:pass
 def wait(mark,at=0):
  end=time.monotonic()+8
  while mark.encode() not in data[at:]:
   if time.monotonic()>end:raise AssertionError((mode,w,'missing',mark,bytes(data[-1500:])))
   drain(.03)
  drain()
 def send(v):os.write(m,v.encode());drain()
 def alive():assert os.waitpid(pid,os.WNOHANG)==(0,0),(mode,'CLI exited')
 def messages():return [x for f in home.glob('sessions/*.json') for x in json.loads(f.read_text())['messages']]
 def active(n):return data.rfind(f'\x1b[?{n}h'.encode())>data.rfind(f'\x1b[?{n}l'.encode())
 try:
  wait('auxim' if mode in ('inline','dumb') else 'Message Auxim...');
  if mode!='dumb':assert flags[screen]==1 and queries>=2
  initial=termios.tcgetattr(m);assert bool(initial[3]&termios.ISIG)==(mode!='raw-key')
  # Encoded Ctrl+C clears the idle draft; encoded Enter submits after newline edits.
  send('discard me');send('\x1b[99;5u');alive();send('first\x1b[106;5u中文🙂');assert not calls,'Ctrl+J submitted'
  send('\x1b[13;2u');send('third');assert not calls,'Shift Enter submitted'
  send('\x1b[27;2;13~fourth');assert not calls
  at=len(data);send('\x1b[13u');wait('done in',at);assert calls[-1]=='first\n中文🙂\nthird\nfourth',calls
  at=len(data);send('abc\x1b[1;129DQ\x1b[1;65C\r');wait('done in',at);assert messages()[-2]['content']=='abQc'
  send('idle selection preserved');send('\x1b[97;5u');send('\x1b[99;5u');alive()
  at=len(data);send('\x1b[13u');wait('done in',at);assert calls[-1]=='idle selection preserved',calls
  for prompt in ['stream','repeat','signal']:
   saved=messages();at=len(data);send(prompt+'\x1b[13u');wait('partial-'+prompt,at)
   if prompt=='signal':os.kill(pid,signal.SIGINT);drain()
   elif prompt=='repeat':
    for _ in range(5):send('\x1b[99;5u')
   else:send('\x1b[99;5u')
   wait('Cancelled current turn',at);alive();assert messages()==saved,'partial response persisted'
   if mode not in ('inline','dumb'):assert b'Cancelled preview' in data[at:];assert b'keep this preview' in data[at:]
   if mode!='dumb':assert active(2004),'paste mode not restored'
   at=len(data);send('next-'+prompt+'\x1b[13u');wait('done in',at);assert messages()[-2]['content']=='next-'+prompt
  at=len(data);saved=messages();send('approve\x1b[13u');wait('Allow once',at);assert mode=='dumb' or flags[screen]==0;send('\x1b[99;5u');wait('Cancelled current turn',at);alive()
  assert messages()==saved and not (home/'tmp/should-not-exist').exists();assert not (home/'approvals.json').exists()
  at=len(data);send('tool\x1b[13u');end=time.monotonic()+5
  while not slow.is_set() and time.monotonic()<end:
   drain()
   if b'Allow once' in data[at:]:send('\x1b[13u')
  assert slow.is_set(),'web tool did not start';send('\x1b[99;5u');wait('Cancelled current turn',at);alive()
  at=len(data);send('after-tool\x1b[13u');wait('done in',at)
  at=len(data);send('approve\r');wait('Allow once',at);send('\x1b[13u');wait('done in',at);alive();assert not (home/'tmp/should-not-exist').exists()
  if (mode,w)==('alternate',80):
   send('\x1b[200~abcdef\x1b[201~');send('\x1b[<0;9;21M');assert active(1002)
   send('\x1b[<32;12;21M');send('\x1b[<0;12;21m');assert not active(1002)
   at=len(data);send('Q\r');wait('done in',at);assert messages()[-2]['content']=='aQef'
   at=len(data);send('/resume\r');wait('Resume',at);send('\x1b[27u');assert flags[screen]==1
  if (mode,w) in [('alternate',80),('inline',80)]:
   at=len(data);send('/auth set-api-key\r');wait('API key:',at);drain(.2);assert flags[screen]==0
   secret_start=len(data);send('\x1b[13u');send('temporary-test-secret');assert b'temporary-test-secret' not in data[secret_start:]
   send('\r');wait('saved to',at)
   if mode!='inline':wait('Press Enter to return',at);send('\r')
   drain(.3);assert flags[screen]==1 and len(stacks[screen])==1
   at=len(data);send('/auth set-api-key\r');wait('API key:',at);drain(.2);assert flags[screen]==0
   os.kill(pid,signal.SIGINT);wait('No API key provided.',at)
   if mode!='inline':wait('Press Enter to return',at);send('\r')
   drain(.3);assert flags[screen]==1
   at=len(data);send("//printf 'protocol-%s-ok\\n' foreground\r");wait('protocol-foreground-ok',at)
   if mode!='inline':wait('Press Enter to return',at);send('\r')
   drain(.3);assert child_checks>0 and flags[screen]==1
   # Paste remains atomic, wheel opens History, encoded Esc returns without submission.
   at=len(data);send('\x1b[200~unsent\n中文🙂\x1b[201~');send('\x1b[<65;2;2M')
   if mode!='inline':wait('History',at)
   send('\x1b[27u');send('\x1b[97;5u');send('\x1b[127u')
   at=len(data);send('after-navigation\r');wait('done in',at);assert messages()[-2]['content']=='after-navigation'
  if (mode,w)==('alternate',80):
   other_env=dict(env,AUXIM_HOME=str(home/'noninteractive'))
   result=subprocess.run(['dotnet',cli,'chat','plain-output'],stdin=subprocess.DEVNULL,capture_output=True,env=other_env,timeout=8)
   assert result.returncode==0 and b'\x1b' not in result.stdout+result.stderr
   previous_calls=len(calls)
   child=subprocess.Popen(['dotnet',cli,'chat','stream'],stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE,env=other_env)
   deadline=time.monotonic()+5
   while len(calls)==previous_calls and time.monotonic()<deadline:drain(.03)
   assert len(calls)>previous_calls
   child.send_signal(signal.SIGINT);out,err=child.communicate(timeout=5)
   assert child.returncode in (-signal.SIGINT,130) and not out and b'\x1b' not in err
   assert flags[screen]==1
  send('/exit\x1b[13u');_,exitstatus=os.waitpid(pid,0);assert os.waitstatus_to_exitcode(exitstatus)==0;drain()
  assert not stacks[False] and not stacks[True] and flags=={False:8,True:0}
  assert not active(1000) and not active(1002) and not active(2004)
  if mode=='dumb':assert b'\x1b' not in data
  restored=termios.tcgetattr(m);assert restored[3]&termios.ICANON and restored[3]&termios.ECHO
  reports.append(dict(mode=mode,width=w,CSI_u_ctrl_c=True,protocol_injection=True,negotiation=mode!='dumb',balanced_stacks=True,direct_SIGINT=True,repeated_interrupt=True,approval=True,web_tool=True,newlines=True,session_safe=True,termios_restored=True));print('PASS',reports[-1],flush=True)
 finally:
  (root/(mode+str(w)+'.ansi')).write_bytes(data)
  if exitstatus is None:
   try:os.kill(pid,signal.SIGKILL);os.waitpid(pid,0)
   except ProcessLookupError:pass
  os.close(m);server.shutdown();server.server_close()
(root/'results.json').write_text(json.dumps(reports,indent=2))

print('PTY artifacts:', root, flush=True)
