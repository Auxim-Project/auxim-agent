# PTY protocol injection, not a physical-key test. Run after Release build.
import tempfile
import os,pty,termios,fcntl,struct,select,time,json,threading,http.server,signal,sys,uuid
from pathlib import Path
root=Path(tempfile.mkdtemp(prefix='auxim-kitty-'));repo=Path(__file__).resolve().parents[2];cli=str(repo/'src/Auxim.Cli/bin/Release/net10.0/auxim.dll');reports=[]
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
 def drain(t=.12):
  end=time.monotonic()+t
  while time.monotonic()<end:
   if select.select([m],[],[],.01)[0]:
    try:
     b=os.read(m,65536);data.extend(b)
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
  wait('auxim' if mode in ('inline','dumb') else 'Message Auxim...');initial=termios.tcgetattr(m);assert bool(initial[3]&termios.ISIG)==(mode!='raw-key')
  # Encoded Ctrl+C clears the idle draft; encoded Enter submits after newline edits.
  send('discard me');send('\x1b[99;5u');alive();send('first\x1b[106;5u中文🙂');assert not calls,'Ctrl+J submitted'
  send('\x1b[13;2u');send('third');assert not calls,'Shift Enter submitted'
  send('\x1b[27;2;13~fourth');assert not calls
  at=len(data);send('\x1b[13u');wait('done in',at);assert calls[-1]=='first\n中文🙂\nthird\nfourth',calls
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
  at=len(data);saved=messages();send('approve\x1b[13u');wait('Allow once',at);send('\x1b[99;5u');wait('Cancelled current turn',at);alive()
  assert messages()==saved and not (home/'tmp/should-not-exist').exists();assert not (home/'approvals.json').exists()
  at=len(data);send('tool\x1b[13u');end=time.monotonic()+5
  while not slow.is_set() and time.monotonic()<end:
   drain()
   if b'Allow once' in data[at:]:send('\x1b[13u')
  assert slow.is_set(),'web tool did not start';send('\x1b[99;5u');wait('Cancelled current turn',at);alive()
  at=len(data);send('after-tool\x1b[13u');wait('done in',at)
  at=len(data);send('approve\r');wait('Allow once',at);send('\x1b[13u');wait('done in',at);alive();assert not (home/'tmp/should-not-exist').exists()
  send('/exit\x1b[13u');_,exitstatus=os.waitpid(pid,0);assert os.waitstatus_to_exitcode(exitstatus)==0;drain()
  assert not active(1000) and not active(1002) and not active(2004)
  if mode=='dumb':assert b'\x1b' not in data
  restored=termios.tcgetattr(m);assert restored[3]&termios.ICANON and restored[3]&termios.ECHO
  reports.append(dict(mode=mode,width=w,CSI_u_ctrl_c=True,protocol_injection=True,direct_SIGINT=True,repeated_interrupt=True,approval=True,web_tool=True,newlines=True,session_safe=True,termios_restored=True));print('PASS',reports[-1],flush=True)
 finally:
  (root/(mode+str(w)+'.ansi')).write_bytes(data)
  if exitstatus is None:
   try:os.kill(pid,signal.SIGKILL);os.waitpid(pid,0)
   except ProcessLookupError:pass
  os.close(m);server.shutdown();server.server_close()
(root/'results.json').write_text(json.dumps(reports,indent=2))

print('PTY artifacts:', root, flush=True)
