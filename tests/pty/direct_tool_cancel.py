"""Real CLI PTY regression, using simulated Kitty reports and local HTTP only."""
import os,pty,fcntl,termios,struct,select,time,re,json,signal,threading,http.server,subprocess,tempfile
from pathlib import Path
repo=Path(__file__).resolve().parents[2];cli=repo/'src/Auxim.Cli/bin/Release/net10.0/auxim.dll';root=Path(tempfile.mkdtemp(prefix='auxim-direct-tool-'));reports=[]
class Server(http.server.ThreadingHTTPServer):daemon_threads=True
started=threading.Event()
class Handler(http.server.BaseHTTPRequestHandler):
 def log_message(self,*args):pass
 def do_GET(self):
  started.set();time.sleep(2)
  try:self.send_response(200);self.end_headers();self.wfile.write(b'local tool response')
  except OSError:pass
server=Server(('127.0.0.1',0),Handler);threading.Thread(target=server.serve_forever,daemon=True).start()
for mode in ('alternate','inline','legacy','no-color','narrow','dumb'):
 if os.environ.get('ONLY') and mode not in os.environ['ONLY'].split(','):continue
 home=root/mode;(home/'sessions').mkdir(parents=True);(home/'current_session').write_text('kept')
 session=home/'sessions/kept.json';session.write_text(json.dumps(dict(id='kept',title='Kept',createdAt='2026-11-18T00:00:00Z',updatedAt='2026-11-18T00:00:00Z',messages=[dict(role='user',content='kept question'),dict(role='assistant',content='kept reply')])));original=session.read_bytes()
 env=dict(os.environ,AUXIM_HOME=str(home),AUXIM_PROVIDER='local',TERM='dumb' if mode=='dumb' else 'xterm-256color',COLORTERM='truecolor');env.pop('AUXIM_NO_ALT_SCREEN',None);env.pop('NO_COLOR',None)
 if mode in ('inline','dumb'):env['AUXIM_NO_ALT_SCREEN']='1'
 if mode=='no-color':env['NO_COLOR']='1'
 fixed=mode not in ('inline','dumb');supported=mode not in ('legacy','dumb');width=32 if mode=='narrow' else 80
 pid,m=pty.fork()
 if pid==0:os.chdir(repo);os.execvpe('dotnet',['dotnet',str(cli)],env)
 fcntl.ioctl(m,termios.TIOCSWINSZ,struct.pack('HHHH',24,width,0,0));data=bytearray();wire=b'';screen=False;flags={False:0,True:0};stacks={False:[],True:[]};exited=False
 def drain(t=.15):
  global wire,screen
  end=time.monotonic()+t
  while time.monotonic()<end:
   if not select.select([m],[],[],.01)[0]:continue
   try:b=os.read(m,65536)
   except OSError:continue
   data.extend(b);wire+=b;last=0
   for match in re.finditer(rb'\x1b\[[0-?]*[ -/]*[@-~]',wire):
    code=match.group();last=match.end()
    if code==b'\x1b[?1049h':screen=True
    elif code==b'\x1b[?1049l':assert not stacks[True];screen=False
    elif code==b'\x1b[?u' and supported:os.write(m,('\x1b[?%du'%flags[screen]).encode())
    elif code in (b'\x1b[>1u',b'\x1b[>0u'):stacks[screen].append(flags[screen]);flags[screen]=int(chr(code[-2]))
    elif code==b'\x1b[<u':assert stacks[screen];flags[screen]=stacks[screen].pop()
    elif code==b'\x1b[6n':os.write(m,b'\x1b[21;8R')
   tail=wire[last:];at=tail.rfind(b'\x1b');wire=tail[at:] if at>=0 else b''
 def wait(text,at=0):
  end=time.monotonic()+8
  while text.encode() not in data[at:]:
   if time.monotonic()>end:raise AssertionError((mode,text,bytes(data[-1200:])))
   drain(.03)
  drain()
 def send(text):os.write(m,text.encode());drain()
 def alive():assert os.waitpid(pid,os.WNOHANG)==(0,0)
 def command(text):at=len(data);send(text+'\r');return at
 def success(text,marker):
  at=command(text)
  if fixed:wait('Press Enter to return',at);send('\r')
  else:wait('Tool completed.',at)
  assert marker.encode() in data[at:];alive()
 def approval():
  at=command('/tool run file.write path=/tmp/blocked content=harmless');wait('Allow',at);return at
 try:
  wait('Message Auxim...' if fixed else 'auxim');drain(.3)
  # Physical ETX on controlling PTY -> SIGINT while approval owns compatible flags=0.
  at=approval();assert flags[screen]==0;send('2');send('\x03');wait('Tool wait cancelled',at);alive()
  assert not (home/'tmp/blocked').exists() and not (home/'approvals.json').exists()
  # Encoded Ctrl+C must take the same turn token path even in approval.
  at=approval();send('2');send('\x1b[99;5u');wait('Tool wait cancelled',at);alive();assert not (home/'tmp/blocked').exists()
  # Cooperative web tool cancellation while Kitty is really active.
  started.clear();at=command('/tool run web.fetch url=http://127.0.0.1:%d/slow'%server.server_port)
  end=time.monotonic()+5
  while not started.is_set() and time.monotonic()<end:
   drain()
   if b'Decision' in data[at:]:send('\r')
  assert started.is_set();assert not supported or flags[screen]==1
  for _ in range(4):send('\x1b[99;5u' if supported else '\x03')
  wait('Tool wait cancelled',at);alive();success('/tool run echo text=NEXT_TOOL_OK','NEXT_TOOL_OK')
  # Normal failure and thrown tool lookup exception don't exit the shell.
  at=command('/tool run definitely-missing-tool');wait('Tool failed:',at);alive()
  at=command('/tool run file.read path=/tmp/absent-audit-file');wait('Tool failed:',at);alive()
  at=approval();send('3\r');wait('Feedback',at);send('audit denial\r');wait('Tool failed:',at);assert not (home/'tmp/blocked').exists()
  for attack in ('\x1b[200~2\r\x1b[201~','\x1b[99999;5u\r','\x1b[<0;2;2M2\r'):
   at=approval();send(attack);wait('Tool failed:',at);assert not (home/'tmp/blocked').exists() and not (home/'approvals.json').exists()
  # Legitimate Allow once still executes, and late cancellation cannot undo it.
  at=approval();send('\r')
  end=time.monotonic()+5
  while not (home/'tmp/blocked').exists() and time.monotonic()<end:drain()
  assert (home/'tmp/blocked').read_text()=='harmless'
  if fixed:wait('Press Enter to return',at);send('\x03');send('\r')
  else:wait('Tool completed.',at)
  assert (home/'tmp/blocked').exists();success('/tool run echo text=AFTER_ALLOW','AFTER_ALLOW')
  assert session.read_bytes()==original and (home/'current_session').read_text()=='kept'
  send('/exit\r');_,status=os.waitpid(pid,0);exited=True;drain();assert os.waitstatus_to_exitcode(status)==0
  assert not stacks[False] and not stacks[True] and flags=={False:0,True:0}
  attrs=termios.tcgetattr(m);assert attrs[3]&termios.ICANON and attrs[3]&termios.ECHO
  if mode=='dumb':assert b'\x1b' not in data
  reports.append(dict(mode=mode,approval_cancel=True,tool_cancel=True,repeat_cancel=True,continue_after_cancel=True,attacks_denied=True,allow_once=True,session_preserved=True,terminal_restored=True));print('PASS',reports[-1],flush=True)
 finally:
  (root/(mode+'.ansi')).write_bytes(data)
  if not exited:
   try:os.kill(pid,signal.SIGKILL);os.waitpid(pid,0)
   except ProcessLookupError:pass
  os.close(m)
# The public argv entry still has its original stdout and exit status.
env=dict(os.environ,AUXIM_HOME=str(root/'noninteractive'),AUXIM_PROVIDER='local',TERM='dumb',NO_COLOR='1')
p=subprocess.run(['dotnet',str(cli),'tool','run','echo','text=SCRIPT_OUTPUT'],capture_output=True,env=env);assert p.returncode==0 and p.stdout.strip()==b'SCRIPT_OUTPUT' and not p.stderr
p=subprocess.run(['dotnet',str(cli),'tool','run','definitely-missing-tool'],capture_output=True,env=env);assert p.returncode==1 and b'\x1b' not in p.stdout+p.stderr
started.clear()
p=subprocess.Popen(['dotnet',str(cli),'tool','run','web.fetch','url=http://127.0.0.1:%d/slow'%server.server_port],stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE,env=env)
assert started.wait(5),'noninteractive web tool did not start'
p.send_signal(signal.SIGINT);out,err=p.communicate(timeout=5)
assert p.returncode in (-signal.SIGINT,130) and not out and b'\x1b' not in err
print('PASS noninteractive stdout/stderr, exit codes and SIGINT',flush=True)
server.shutdown();(root/'results.json').write_text(json.dumps(reports,indent=2));print('Artifacts:',root)
