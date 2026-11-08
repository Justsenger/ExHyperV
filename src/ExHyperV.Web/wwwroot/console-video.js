// The browser decodes H.264 through its native WebRTC video path.
// WebSocket carries session signalling and Core input, never pixel payloads.
export class BrowserConsole {
  constructor(video, url, report) {
    this.video = video; this.report = report; this.epoch = 0; this.closed = false;
    this.source = {}; this.drawn = 0; this.maxGap = 0; this.lastDraw = 0; this.rtt = 0;
    this.socket = new WebSocket(url);
    let chain = Promise.resolve();
    this.socket.onmessage = event => {
      chain = chain.then(() => this.message(JSON.parse(event.data))).catch(error => this.fail(error.message));
    };
    this.socket.onerror = () => this.fail('无法连接控制台服务。');
    this.socket.onclose = () => { if (!this.closed) this.fail('控制台连接已关闭。'); };
    this.timer = setInterval(() => { this.send({type:'ping',time:performance.now()}); void this.statistics(); }, 1000);
    video.onplaying = () => { if (!this.closed) report({type:'connected',canInput:this.canInput}); };
    const frame = (now, metadata) => {
      if (this.closed) return;
      if (this.lastDraw) this.maxGap = Math.max(this.maxGap, now - this.lastDraw);
      this.lastDraw = now;
      // The compositor can present frames while the main thread misses a callback.
      // Count presented frames, not just JavaScript callback invocations.
      const count = metadata.presentedFrames;
      this.drawn += this.lastPresented != null && count >= this.lastPresented ? count - this.lastPresented : 1;
      this.lastPresented = count;
      this.callback = video.requestVideoFrameCallback(frame);
    };
    this.callback = video.requestVideoFrameCallback(frame);
  }
  send(message) {
    if (!this.closed && this.socket.readyState === WebSocket.OPEN) {
      if (this.socket.bufferedAmount > 256 * 1024) { this.fail('控制连接积压，请重新连接。'); return; }
      this.socket.send(JSON.stringify(message));
    }
  }
  fail(message) { if (!this.closed) { this.stop(); this.report({type:'error',message}); } }
  async message(message) {
    if (this.closed) return;
    if (message.type === 'connecting') this.canInput = message.capabilities?.input === true;
    else if (message.type === 'disconnected') this.fail(message.message);
    else if (message.type === 'pong') this.rtt = performance.now() - message.time;
    else if (message.type === 'media') {
      this.peer?.close(); this.epoch = message.epoch; this.source = {}; this.previous = null;
      this.pendingIce = []; this.localIce = []; this.answerSent = false;
      this.drawn = this.maxGap = this.lastDraw = 0; this.sampleTime = performance.now();
      this.lastPresented = null;
      const epoch = this.epoch, pc = this.peer = new RTCPeerConnection({iceServers:[]});
      this.video.srcObject = null;
      pc.ontrack = event => {
        if (this.peer !== pc || this.closed) return;
        this.video.srcObject = event.streams[0] ?? new MediaStream([event.track]);
        void this.video.play().catch(error => this.fail(error.message));
      };
      pc.onicecandidate = event => {
        if (this.peer !== pc || this.closed || !event.candidate) return;
        const candidate = {type:'ice',epoch,line:event.candidate.sdpMLineIndex,candidate:event.candidate.candidate};
        if (this.answerSent) this.send(candidate); else this.localIce.push(candidate);
      };
      pc.onconnectionstatechange = () => {
        if (this.peer === pc && pc.connectionState === 'failed') this.fail('视频连接失败，请重新连接。');
      };
      this.report({...message,type:'size'});
    } else if (message.epoch === this.epoch && this.peer) {
      const pc = this.peer;
      if (message.type === 'offer') {
        await pc.setRemoteDescription({type:'offer',sdp:message.sdp});
        for (const candidate of this.pendingIce) await pc.addIceCandidate(candidate);
        this.pendingIce = [];
        const answer = await pc.createAnswer(); await pc.setLocalDescription(answer);
        if (this.closed || this.peer !== pc) return;
        this.send({type:'answer',epoch:this.epoch,sdp:answer.sdp}); this.answerSent = true;
        for (const candidate of this.localIce) this.send(candidate);
        this.localIce = [];
      } else if (message.type === 'ice') {
        const candidate = {candidate:message.candidate,sdpMLineIndex:message.line};
        if (pc.remoteDescription) await pc.addIceCandidate(candidate);
        else if (this.pendingIce.length < 64) this.pendingIce.push(candidate);
        else throw new Error('视频协商候选项过多。');
      } else if (message.type === 'mediaStats') this.source = message;
    }
  }
  async statistics() {
    const pc = this.peer;
    if (!pc || this.collecting || this.closed) return;
    this.collecting = true;
    try {
      const stats = await pc.getStats();
      if (this.closed || this.peer !== pc) return;
      let received;
      stats.forEach(s => { if (s.type === 'inbound-rtp' && s.kind === 'video') received = s; });
      if (!received) return;
      const now = performance.now(), seconds = (now - this.sampleTime) / 1000;
      const previous = this.previous, elapsed = previous ? (received.timestamp - previous.timestamp) / 1000 : 0;
      const decoded = previous ? received.framesDecoded - previous.framesDecoded : 0;
      const emitted = previous ? received.jitterBufferEmittedCount - previous.jitterBufferEmittedCount : 0;
      this.report({...this.source, type:'stats',
        fps:this.drawn / seconds, decodedFps:elapsed > 0 ? decoded / elapsed : 0,
        mbps:elapsed > 0 ? (received.bytesReceived - previous.bytesReceived) * 8 / elapsed / 1e6 : 0,
        decodeMs:decoded > 0 ? (received.totalDecodeTime - previous.totalDecodeTime) * 1000 / decoded : 0,
        bufferMs:emitted > 0 ? (received.jitterBufferDelay - previous.jitterBufferDelay) * 1000 / emitted : 0,
        rtt:this.rtt, maxGap:this.maxGap, browserDropped:received.framesDropped ?? 0,
        freezes:received.freezeCount ?? 0, freezeSeconds:received.totalFreezesDuration ?? 0,
        receivedFrames:received.framesReceived ?? 0, keyFrames:received.keyFramesDecoded ?? 0,
        pliCount:received.pliCount ?? 0, nackCount:received.nackCount ?? 0,
        packetsLost:received.packetsLost ?? 0, hidden:document.hidden});
      this.previous = received; this.sampleTime = now; this.drawn = this.maxGap = 0;
    } catch (error) { if (!this.closed && this.peer === pc) this.fail(error.message); }
    finally { this.collecting = false; }
  }
  stop() {
    if (this.closed) return;
    this.closed = true; clearInterval(this.timer);
    this.video.cancelVideoFrameCallback(this.callback); this.video.onplaying = null;
    this.peer?.close(); this.socket.close(); this.video.srcObject = null;
  }
}
