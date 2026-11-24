# PTY protocol injection, not a physical-key test. Run after Release build.
import subprocess
import re
import tempfile
import os,pty,termios,fcntl,struct,select,time,json,threading,http.server,signal,sys,uuid
from pathlib import Path
from approval_grid import frame
root=Path(tempfile.mkdtemp(prefix='auxim-kitty-protocol-'));repo=Path(__file__).resolve().parents[2];cli=str(repo/'src/Auxim.Cli/bin/Release/net10.0/auxim.dll');reports=[]
for mode,w in [('alternate',80),('alternate',32),('inline',80),('no-color',80),('dumb',80),('raw-key',80)]:
 if os.environ.get('ONLY') and mode not in os.environ['ONLY'].split(','):continue
 home=root/(mode+str(w)+'-'+uuid.uuid4().hex);slow=threading.Event();approval_selection_ready=threading.Event();review_ready=threading.Event();continue_live=threading.Event();finish_live=threading.Event();tail_live=threading.Event();review_finish=threading.Event();failure_ready=threading.Event();calls=[]
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
    elif prompt=='workspace-live':
     delta({'content':'first live frame'})
     assert continue_live.wait(8)
     delta({'content':'\n' + '\n'.join(f'LIVE-LINE-{n:02d} 中文' for n in range(40))+'\nlatest-live-tail'})
     assert tail_live.wait(8)
     delta({'content':'\nnew-lines-while-paused'})
     assert finish_live.wait(8)
    elif prompt=='selection-fixture 中文':
     delta({'content':'Assistant 多行文本\n\n```sh\nprintf COPY_LINE\n```'})
    elif prompt=='workspace-failure':
     delta({'content':'preview-before-failure'})
     assert failure_ready.wait(8)
     delta({'tool_calls':[{'index':0,'id':'failed-read','function':{'name':'file.read','arguments':json.dumps({'path':'/tmp/no-workspace-test-file'})}}]})
    elif prompt=='embedded-review':
     if any(m['role']=='tool' for m in body['messages']):
      if (mode,w)==('alternate',80):assert review_finish.wait(8)
      delta({'content':'continued after approval'})
     else:
      delta({'content':'preview before approval'})
      assert review_ready.wait(8)
      args={'path':'/tmp/embedded-review','content':'BEGIN-'+('x'*1800)+'-END_RISK_DETAILS'}
      delta({'tool_calls':[{'index':0,'id':'review','function':{'name':'file.write','arguments':json.dumps(args)}}]})
    elif prompt in ('approve','tool','approval-cancel-with-selection'):
     if prompt=='approval-cancel-with-selection':
      delta({'content':'select-before-approval'})
      assert approval_selection_ready.wait(8)
     name='web.fetch' if prompt=='tool' else 'file.write';args={'url':'http://127.0.0.1:%d/slow'%server.server_port} if prompt=='tool' else {'path':'/tmp/should-not-exist','content':'bad'}
     delta({'tool_calls':[{'index':0,'id':'call','function':{'name':name,'arguments':json.dumps(args)}}]})
    else:delta({'content':'OK '+prompt})
    self.wfile.write(b'data: [DONE]\n\n');self.wfile.flush()
   except (OSError,ConnectionError):pass
 server=Server(('127.0.0.1',0),Handler)
 env=dict(os.environ,AUXIM_HOME=str(home),AUXIM_PROVIDER='openai-compatible',AUXIM_MODEL='test',AUXIM_BASE_URL='http://127.0.0.1:%d/v1'%server.server_port,AUXIM_API_KEY='fake',TERM='xterm-256color',COLORTERM='truecolor')
 clipboard=home/'clipboard-output'
 clipboard_fail=home/'clipboard-fail'
 if (mode,w)==('alternate',80):
  home.mkdir()
  fake_clipboard=home/('pbcopy' if sys.platform=='darwin' else 'wl-copy')
  fake_clipboard.write_text('#!/bin/sh\n[ ! -f "$AUXIM_TEST_CLIPBOARD_FAIL" ] || exit 1\nexec /bin/cat > "$AUXIM_TEST_CLIPBOARD"\n')
  fake_clipboard.chmod(0o700)
  env.update(PATH=str(home)+os.pathsep+env['PATH'],AUXIM_TEST_CLIPBOARD=str(clipboard),AUXIM_TEST_CLIPBOARD_FAIL=str(clipboard_fail),WAYLAND_DISPLAY='auxim-test',DISPLAY='')
  env.pop('AUXIM_CLIPBOARD',None)
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
 def drag_text(text):
  rows,_=frame(bytes(data),80,24)
  y,row=next((i+1,r) for i,r in enumerate(rows) if text in r)
  x=row.index(text)+1;end_x=x+len(text)-1
  send(f'\x1b[<0;{x};{y}M')
  assert active(1002)
  send(f'\x1b[<32;{end_x};{y}M\x1b[<0;{end_x};{y}m')
  assert not active(1002)
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
   marker='HISTORY_ONLY_SENTINEL'
   at=len(data);send(marker+'\r');wait('done in',at)
   at=len(data);send('ordinary turn before workspace test\r');wait('done in',at)
   idle, _=frame(bytes(data),80,24)
   start=len(data);send('workspace-live\r');wait('first live frame',start)
   during, cursor=frame(bytes(data),80,24)
   assert 'AUXIM / Main' in idle[0] and 'Ready' in idle[0]
   assert 'AUXIM / Main' in during[0] and 'Running' in during[0]
   assert any('Live preview' in row for row in during) and 20 <= cursor[0] <= 24
   assert active(1000) and active(1006) and not active(1002) and not active(1007)
   assert marker.encode() not in data[start:]
   at=len(data);send('\x1b[<65;2;2M');wait('Follow live',at)
   live, _=frame(bytes(data),80,24)
   assert 'AUXIM / History' in live[0] and 'not saved' in '\n'.join(live)
   drag_text('first live frame')
   continue_live.set();drain(.3)
   assert 'first live frame' in '\n'.join(frame(bytes(data),80,24)[0])
   assert 'Copy · selected' in '\n'.join(frame(bytes(data),80,24)[0])
   assert termios.tcgetattr(m)[3]&termios.ISIG
   assert termios.tcgetattr(m)[6][termios.VINTR] != initial[6][termios.VINTR]
   selected_at=len(data)
   send('\x03')
   wait('Copied.',selected_at)
   assert clipboard.read_text()=='first live frame'
   clipboard_fail.touch()
   selected_at=len(data);send('\x1b[99;5u')
   wait('rejected the copy.',selected_at)
   assert clipboard.read_text()=='first live frame'
   assert 'Running' in frame(bytes(data),80,24)[0][0]
   clipboard_fail.unlink()
   send('\x1b[27u')
   assert termios.tcgetattr(m)[6][termios.VINTR] == initial[6][termios.VINTR]
   send('\x1b[F')
   wait('latest-live-tail',at)
   streamed, streamed_cursor=frame(bytes(data),80,24)
   assert 'latest-live-tail' in '\n'.join(streamed) and 20 <= streamed_cursor[0] <= 24
   at=len(data);send('\x1b[<64;2;2M');wait('Paused',at)
   paused, _=frame(bytes(data),80,24)
   send('\x1bOQ')
   assert not active(1000) and not active(1006)
   frozen, _=frame(bytes(data),80,24)
   assert frozen[:19]==paused[:19] and 'Copy frozen page' in '\n'.join(frozen)
   quiet=len(data);tail_live.set();drain(.35)
   assert len(data)==quiet and frame(bytes(data),80,24)[0]==frozen
   send('\x1bOQ');assert active(1000) and active(1006)
   at=len(data);send('\x1b[F');wait('new-lines-while-paused',at)
   following, _=frame(bytes(data),80,24)
   assert 'Follow live' in '\n'.join(following)
   fcntl.ioctl(m,termios.TIOCSWINSZ,struct.pack('HHHH',24,32,0,0))
   os.kill(pid,signal.SIGWINCH);drain(.25)
   narrow, narrow_cursor=frame(bytes(data),32,24)
   assert 'History' in narrow[0] and 20 <= narrow_cursor[0] <= 24
   assert any('Working...' in row for row in narrow[19:])
   fcntl.ioctl(m,termios.TIOCSWINSZ,struct.pack('HHHH',24,80,0,0))
   os.kill(pid,signal.SIGWINCH);drain(.2)
   # Esc followed by complete stale wheels in the same navigation batch
   # and an unfinished report whose tail arrives on Main.
   send('\x1b[27u\x1b[<65;2;2M\x1b[<64;2;2M\x1b[<65;2;')
   back, _=frame(bytes(data),80,24)
   assert 'AUXIM / Main' in back[0] and 'Running' in back[0]
   send('2M')
   assert 'AUXIM / Main' in frame(bytes(data),80,24)[0][0]
   send('\x1b[<64;2;2M')
   assert 'AUXIM / History' in frame(bytes(data),80,24)[0][0]
   send('\x1b[F')
   selected_text='new-lines-while-paused'
   drag_text(selected_text)
   at=len(data)
   send('\x1b[99;9u' if sys.platform=='darwin' else '\x1b[99;5u')
   wait('Copied.',at)
   assert clipboard.read_text()==selected_text
   assert 'Running' in frame(bytes(data),80,24)[0][0]
   send('\x1b[<65;2;')
   at=len(data);finish_live.set();wait('Ready',at)
   send('2M')
   completed, _=frame(bytes(data),80,24)
   assert 'AUXIM / History' in completed[0] and 'Ready' in completed[0]
   assert 'streaming, not saved' not in '\n'.join(completed)
   assert 'Copy · selected' in '\n'.join(completed)
   assert any('new-lines-while-paused' in row for row in completed)
   clipboard.unlink()
   at=len(data);send('\x1b[99;5u');wait('Copied.',at)
   assert clipboard.read_text()==selected_text
   send('\x1b[27u')  # Clear the remapped range before leaving History.
   assert messages()[-1]['content'].count('new-lines-while-paused')==1
   assert b'\x1b[2J' not in data[start:]
   for name, rows in [('main',during),('live',streamed),('paused',paused),('frozen',frozen),('narrow',narrow),('completed',completed)]:
    (root/f'live-history-{name}.txt').write_text('\n'.join(rows)+'\n')
   send('\x1b[27u')
   assert 'AUXIM / Main' in frame(bytes(data),80,24)[0][0]
   saved=messages();at=len(data);send('stream\r');wait('partial-stream',at)
   send('\x1b[<65;2;2M')
   at=len(data);send('\x03');wait('Cancelled · not saved',at)
   cancelled, _=frame(bytes(data),80,24)
   assert 'AUXIM / History' in cancelled[0] and 'not saved' in '\n'.join(cancelled)
   assert 'keep this preview' in '\n'.join(cancelled) and messages()==saved
   (root/'live-history-cancelled.txt').write_text('\n'.join(cancelled)+'\n')
   send('\x1b[27u')
   # A process-directed signal still cancels while ETX is accepted as a key.
   at=len(data);send('stream\r');wait('partial-stream',at)
   send('\x1b[<65;2;2M')
   drag_text('partial-stream')
   assert termios.tcgetattr(m)[6][termios.VINTR] != initial[6][termios.VINTR]
   signal_at=len(data);os.kill(pid,signal.SIGINT);wait('Selection cleared',signal_at)
   assert messages()==saved and clipboard.read_text()==selected_text
   assert termios.tcgetattr(m)[6][termios.VINTR] == initial[6][termios.VINTR]
   send('\x1b[27u')
   at=len(data);send('approval-cancel-with-selection\r');wait('select-before-approval',at)
   send('\x1b[<65;2;2M')
   drag_text('select-before-approval')
   assert 'Copy · selected' in '\n'.join(frame(bytes(data),80,24)[0])
   panel_at=len(data);approval_selection_ready.set();wait('SAFETY REVIEW',panel_at)
   assert not active(1000) and not active(1002)
   assert termios.tcgetattr(m)[6][termios.VINTR] == initial[6][termios.VINTR]
   send('\x03')
   wait('Selection cleared',panel_at)
   assert messages()==saved and not (home/'tmp/should-not-exist').exists()
   assert clipboard.read_text()==selected_text
   send('\x1b[27u')
   at=len(data);send('selection-fixture 中文\r');wait('done in',at)
   at=len(data);send('\x1b[<65;2;2M');wait('History',at)
   assert active(1000) and active(1006)
   history, history_cursor=frame(bytes(data),80,24)
   assert 'AUXIM / History' in history[0]
   assert all(word in '\n'.join(history).replace(' ', '') for word in ('中文','多行文本','printfCOPY_LINE'))
   at=len(data);send('\x1b[114;5u')
   selected, selected_cursor=frame(bytes(data),80,24)
   assert 'Native selection' in '\n'.join(selected)
   assert selected[:19]==history[:19] and selected_cursor==history_cursor
   for mode_id in (9,1000,1002,1003,1005,1006,1007,1015,1016):
    assert f'\x1b[?{mode_id}l'.encode() in data[at:] and not active(mode_id)
   quiet=len(data)
   send('\x1b[<0;2;4M\x1b[<32;8;4M\x1b[<0;8;4m\x1b[<64;2;4M')
   assert len(data)==quiet and frame(bytes(data),80,24)[0]==selected
   send('\x1b[5~');assert frame(bytes(data),80,24)[0]!=selected and not active(1000)
   send('\x1b[6~');assert frame(bytes(data),80,24)[0]==selected and not active(1006)
   fcntl.ioctl(m,termios.TIOCSWINSZ,struct.pack('HHHH',24,60,0,0))
   os.kill(pid,signal.SIGWINCH);drain(.3)
   assert 'AUXIM / History' in frame(bytes(data),60,24)[0][0] and not active(1000)
   fcntl.ioctl(m,termios.TIOCSWINSZ,struct.pack('HHHH',24,80,0,0))
   os.kill(pid,signal.SIGWINCH);drain(.3)
   assert not active(1006)
   send('\x1bOQ');assert active(1000) and active(1006)
   send('\x1b[12~');assert not active(1000) and not active(1006)
   (root/'history-native-selection.txt').write_text('\n'.join(selected)+'\n')
   send('\x1b[27u')
   assert 'AUXIM / Main' in frame(bytes(data),80,24)[0][0] and active(1000)
   saved=messages();at=len(data);send('workspace-failure\r');wait('preview-before-failure',at)
   send('\x1b[<65;2;2M')
   failure_ready.set();drain(.25)
   if b'SAFETY REVIEW' in data[at:]:send('\r')
   wait('Failed · not saved',at)
   failed, _=frame(bytes(data),80,24)
   assert 'AUXIM / History' in failed[0] and 'preview-before-failure' in '\n'.join(failed)
   assert 'file.read  failed' in '\n'.join(failed) and messages()==saved
   (root/'live-history-failed.txt').write_text('\n'.join(failed)+'\n')
   send('\x1b[27u')
  if (mode,w) in [('alternate',32),('no-color',80)]:
   at=len(data);send('\x1b[<65;2;2M');wait('History',at)
   send('\x12');assert not active(1000) and not active(1006) and not active(1007)
   at=len(data);send('\x1b[<64;2;2M');assert len(data)==at
   send('\x1bOQ');assert active(1000) and active(1006)
   send('\x1b[27u');assert active(1000) and active(1006)
  if (mode,w) in [('alternate',80),('no-color',80)]:
   at=len(data);send('embedded-review\r');wait('preview before approval',at)
   if (mode,w)==('alternate',80):
    send('\x1b[<65;2;2M')
    send('\x1b[5~')
   before, before_cursor=frame(bytes(data),80,24)
   assert 'AUXIM /' in before[0] and 20 <= before_cursor[0] <= 24
   panel_start=len(data);review_ready.set();wait('SAFETY REVIEW',panel_start)
   during, cursor=frame(bytes(data),80,24)
   assert not active(1000) and not active(1006) and not active(1007)
   assert before[:2]==during[:2] and cursor==before_cursor,(before_cursor,cursor)
   assert 'Agent / Session' in during[2] and 'Activity' in during[3]
   assert 'Risk:' in '\n'.join(during) and 'Approval owns input' in '\n'.join(during)
   assert '\x1b[2J' not in data[panel_start:].decode(errors='replace')
   at_idle=len(data);drain(.25);assert len(data)==at_idle, 'stream writer active during approval'
   seen=set()
   for _ in range(12):
    seen.update(frame(bytes(data),80,24)[0])
    send('\x1b[6~')
   assert any('END_RISK_DETAILS' in row for row in seen)
   assert any('Resources:' in row for row in seen)
   assert any('embedded-review' in row for row in seen)
   at_boundary=len(data);send('\x1b[6~');assert len(data)==at_boundary
   send('\x1b[5~')
   _, scrolled_cursor=frame(bytes(data),80,24);assert scrolled_cursor==cursor
   at=len(data);send('\r')
   if (mode,w)==('alternate',80):
    wait('Paused',at)
    restored, _=frame(bytes(data),80,24)
    assert restored[:19]==before[:19] and flags[screen]==1 and active(1000)
    assert 'SAFETY REVIEW' not in '\n'.join(restored)
    (root/'live-history-after-approval.txt').write_text('\n'.join(restored)+'\n')
    send('\x1b[27u')
    review_finish.set()
   wait('continued after approval',at);wait('done in',at)
   after, after_cursor=frame(bytes(data),80,24)
   assert 'AUXIM / Main' in after[0] and 'SAFETY REVIEW' not in '\n'.join(after)
   assert any('Approval requested for file.write' in row for row in after)
   assert any('Approval allowed once for file.write' in row for row in after)
   assert any('file.write: succeeded' in row for row in after)
   assert 20 <= after_cursor[0] <= 24 and flags[screen]==1 and active(1000) and active(1006)
   assert (home/'tmp/embedded-review').read_text().endswith('-END_RISK_DETAILS')
   for name, rows in [('before',before),('during',during),('after',after)]:
    (root/f'{mode}-approval-{name}.txt').write_text('\n'.join(rows)+'\n')
  if (mode,w)==('alternate',80):
   send('\x1b[200~abcdef\x1b[201~');send('\x1b[<0;9;21M');assert active(1002)
   send('\x1b[<32;12;21M');send('\x1b[<0;12;21m');assert not active(1002)
   at=len(data);send('Q\r');wait('done in',at);assert messages()[-2]['content']=='aQef'
   at=len(data);send('/session\r');wait('Sessions',at);send('\x1b[27u');assert flags[screen]==1
  if (mode,w) in [('alternate',80),('inline',80)]:
   at=len(data);send('/auth set-api-key\r');wait('API key:',at);drain(.2);assert flags[screen]==0
   secret_start=len(data);send('\x1b[13u');send('temporary-test-secret');assert b'temporary-test-secret' not in data[secret_start:]
   send('\r');wait('saved to',at)
   if mode!='inline':wait('Press Enter to return',at);send('\r')
   drain(.3);assert flags[screen]==1 and len(stacks[screen])==1
   at=len(data);send('/auth set-api-key\r');wait('API key:',at);drain(.2);assert flags[screen]==0
   os.kill(pid,signal.SIGINT);wait('Configuration cancelled.',at)
   drain(.3);assert flags[screen]==1
   at=len(data);send("//printf 'protocol-%s-ok\\n' foreground\r");wait('protocol-foreground-ok',at)
   assert not active(1000) and not active(1006)
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
