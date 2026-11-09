# Protocol simulation, not physical keyboard validation. No model service needed.
import os,pty,select,subprocess,termios,time,re,tempfile,signal,json
from pathlib import Path
repo=Path(__file__).resolve().parents[2];cli=repo/'src/Auxim.Cli/bin/Release/net10.0/auxim.dll'
root=Path(tempfile.mkdtemp(prefix='auxim-keyboard-diagnostic-'));reports=[]
for case in ('supported','timeout','malformed','late','activation-mismatch','interrupted','signal-exit'):
 m,s=pty.openpty();before=termios.tcgetattr(s);data=bytearray();wire=b'';stack=[];flags=8;queries=0;interrupted=False
 env=dict(os.environ,TERM='xterm-256color',COLORTERM='truecolor')
 p=subprocess.Popen(['dotnet',str(cli),'--diagnose-input'],stdin=s,stdout=s,stderr=s,env=env,start_new_session=True)
 def drain(t=.1):
  global wire,flags,queries,interrupted
  end=time.monotonic()+t
  while time.monotonic()<end:
   if not select.select([m],[],[],.01)[0]:continue
   try:b=os.read(m,65536)
   except OSError:continue
   data.extend(b);wire+=b;last=0
   for match in re.finditer(rb'\x1b\[[0-?]*[ -/]*[@-~]',wire):
    code=match.group();last=match.end()
    if code==b'\x1b[?u':
     queries+=1
     if case in ('supported','signal-exit') or case=='activation-mismatch' and queries==1:
      os.write(m,('\x1b[?%du'%flags).encode())
     elif case=='activation-mismatch':os.write(m,b'\x1b[?0u')
     elif case=='malformed':os.write(m,b'\x1b[?1;2u')
     elif case=='interrupted' and not interrupted:
      os.kill(p.pid,signal.SIGINT);interrupted=True
    elif code==b'\x1b[>1u':stack.append(flags);flags=1
    elif code==b'\x1b[>0u':stack.append(flags);flags=0
    elif code==b'\x1b[<u':assert stack;flags=stack.pop()
   tail=wire[last:];at=tail.rfind(b'\x1b');wire=tail[at:] if at>=0 else b''
 def wait(marker):
  end=time.monotonic()+5
  while marker not in data:
   if time.monotonic()>end:raise AssertionError((case,marker,bytes(data[-1200:])))
   drain()
 try:
  wait(b'Auxim active flags=');drain()
  if case=='supported' or case=='signal-exit':assert flags==1 and len(stack)==1;assert b'active flags=1' in data
  else:assert flags==8 and not stack;assert b'active flags=0' in data
  if case=='late':
   at=len(data);os.write(m,b'\x1b[?1u');drain(.3);assert len(data)==at,'late response leaked';assert not stack
  if case=='signal-exit':os.kill(p.pid,signal.SIGTERM)
  else:
   # Payloads must never appear in diagnostic output.
   os.write(m,b'private-text\x1b[200~paste-secret\r\x1b[201~\r\x1b[13;2u\x1b[106;5u');drain(.2)
   assert b'private-text' not in data and b'paste-secret' not in data
   assert b'-> Submit' in data and data.count(b'-> InsertNewline')==2
   os.write(m,b'\x04')
  p.wait(timeout=5);drain();assert not stack and flags==8
  after=termios.tcgetattr(s);before[3]&=~termios.PENDIN;after[3]&=~termios.PENDIN;assert before==after
  reports.append(dict(case=case,stack_restored=True,termios_restored=True,exit=p.returncode));print('PASS',reports[-1],flush=True)
 finally:
  (root/(case+'.ansi')).write_bytes(data)
  if p.poll() is None:p.kill();p.wait()
  os.close(m);os.close(s)
(root/'results.json').write_text(json.dumps(reports,indent=2));print('Artifacts:',root)
