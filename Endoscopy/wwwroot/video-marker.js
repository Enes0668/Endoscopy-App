/**
 * VideoMarkerEngine - Video İçi İndeksleme & Marker Yöneticisi
 * Video üzerinde zaman damgalı bookmark/marker oluşturma, timeline üzerinde pinleme ve atlama motoru.
 */
(function() {
  const QUICK_TAGS = [
    'Polip', 'Kanama Odağı', 'Biyopsi Alındı', 'Z Çizgisi',
    'Eritem / Gastrit', 'Ülser', 'Retrofleksiyon', 'Çekum / İleoçekal Valv',
    'Polipektomi', 'Divertikül'
  ];

  class VideoMarkerManager {
    constructor() {
      this.currentCapture = null;
      this.markers = [];
      this.injected = false;
      this.videoEl = null;
      this.timelineTrack = null;
      this.timelineProgress = null;
      this.pinsContainer = null;
      this.currentTimeEl = null;
      this.totalTimeEl = null;
      this.markerListEl = null;
      this.customLabelInput = null;
    }

    initUI() {
      if (this.injected) return;
      this.injected = true;

      const overlay = document.createElement('div');
      overlay.id = 'marker-modal-overlay';
      overlay.className = 'marker-modal-overlay';
      overlay.innerHTML = `
        <div class="marker-modal">
          <div class="marker-modal-header">
            <div class="marker-modal-title">
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#f59e0b" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <path d="M19 21l-7-5-7 5V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2z"></path>
              </svg>
              <span>Video İçi İndeksleme & Marker (Bookmark)</span>
              <span id="marker-modal-capture-info" style="font-size:12px;color:var(--muted,#94a3b8);font-weight:400;margin-left:8px;"></span>
            </div>
            <button class="marker-modal-close" onclick="VideoMarker.close()" title="Kapat">
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <line x1="18" y1="6" x2="6" y2="18"></line>
                <line x1="6" y1="6" x2="18" y2="18"></line>
              </svg>
            </button>
          </div>

          <div class="marker-modal-body">
            <!-- Left: Video Player + Timeline -->
            <div class="video-player-pane">
              <div class="player-video-wrap">
                <video id="marker-player-video" playsinline preload="metadata"></video>
              </div>

              <!-- Interactive Timeline with Markers -->
              <div class="timeline-track-container" id="timeline-track-container" title="Zamana atlamak için tıklayın">
                <div class="timeline-track-bar">
                  <div class="timeline-progress-bar" id="timeline-progress-bar"></div>
                  <div id="timeline-pins-container"></div>
                </div>
              </div>

              <!-- Player Controls & Time -->
              <div class="player-controls-bar">
                <div class="player-btn-group">
                  <button class="btn btn-ghost btn-sm" id="marker-play-btn" onclick="VideoMarker.togglePlay()">
                    <svg id="marker-play-icon" width="14" height="14" viewBox="0 0 24 24" fill="currentColor">
                      <polygon points="5 3 19 12 5 21 5 3"></polygon>
                    </svg>
                  </button>
                  <button class="btn btn-ghost btn-sm" onclick="VideoMarker.step(-5)" title="5 sn geri">
                    -5s
                  </button>
                  <button class="btn btn-ghost btn-sm" onclick="VideoMarker.step(5)" title="5 sn ileri">
                    +5s
                  </button>
                </div>

                <div class="player-time-display">
                  <span id="player-current-time">00:00</span> / <span id="player-total-time">00:00</span>
                </div>
              </div>
            </div>

            <!-- Right: Markers List & Quick Add Form -->
            <div class="marker-sidebar-pane">
              <div class="marker-add-box">
                <div style="font-size:12px;font-weight:600;display:flex;justify-content:space-between;align-items:center;">
                  <span>Mevcut Zamana Marker Ekle</span>
                  <span id="marker-add-time-badge" class="marker-item-time">00:00</span>
                </div>

                <!-- Quick Tags -->
                <div class="marker-tag-chips">
                  ${QUICK_TAGS.map(tag => `<button type="button" class="marker-tag-chip" onclick="VideoMarker.addMarkerWithLabel('${tag}')">${tag}</button>`).join('')}
                </div>

                <!-- Custom Input -->
                <div class="marker-input-row">
                  <input type="text" id="marker-custom-label-input" placeholder="Özel not veya lezyon açıklaması..." onkeydown="if(event.key==='Enter'){VideoMarker.addCustomMarker();}" />
                  <button class="btn btn-primary btn-sm" onclick="VideoMarker.addCustomMarker()">Ekle</button>
                </div>
              </div>

              <!-- Marker List -->
              <div style="font-size:12px;font-weight:600;color:var(--muted,#94a3b8);display:flex;justify-content:space-between;align-items:center;">
                <span>Kayıtlı İşaretler</span>
                <span id="marker-count-badge" style="font-size:11px;">0 İşaret</span>
              </div>
              <div class="marker-list-container" id="marker-list-container">
                <!-- JS enjekte eder -->
              </div>
            </div>
          </div>
        </div>
      `;

      document.body.appendChild(overlay);

      // Element referansları
      this.videoEl = document.getElementById('marker-player-video');
      this.timelineTrack = document.getElementById('timeline-track-container');
      this.timelineProgress = document.getElementById('timeline-progress-bar');
      this.pinsContainer = document.getElementById('timeline-pins-container');
      this.currentTimeEl = document.getElementById('player-current-time');
      this.totalTimeEl = document.getElementById('player-total-time');
      this.markerListEl = document.getElementById('marker-list-container');
      this.customLabelInput = document.getElementById('marker-custom-label-input');

      this.bindEvents();
    }

    bindEvents() {
      // Video zaman güncellemesi
      this.videoEl.addEventListener('timeupdate', () => {
        this.updateTimeUI();
      });

      this.videoEl.addEventListener('loadedmetadata', () => {
        this.updateTimeUI();
        this.renderTimelinePins();
      });

      this.videoEl.addEventListener('play', () => {
        const icon = document.getElementById('marker-play-icon');
        if (icon) icon.innerHTML = '<rect x="6" y="4" width="4" height="16"></rect><rect x="14" y="4" width="4" height="16"></rect>';
      });

      this.videoEl.addEventListener('pause', () => {
        const icon = document.getElementById('marker-play-icon');
        if (icon) icon.innerHTML = '<polygon points="5 3 19 12 5 21 5 3"></polygon>';
      });

      // Timeline tıklama ile arama (scrub)
      this.timelineTrack.addEventListener('click', (e) => {
        if (!this.videoEl.duration) return;
        const rect = this.timelineTrack.getBoundingClientRect();
        const pos = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
        this.videoEl.currentTime = pos * this.videoEl.duration;
      });
    }

    formatTime(sec) {
      if (!sec || isNaN(sec) || sec < 0) return '00:00';
      const m = Math.floor(sec / 60);
      const s = Math.floor(sec % 60);
      return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
    }

    updateTimeUI() {
      const cur = this.videoEl.currentTime || 0;
      const dur = this.videoEl.duration || (this.currentCapture?.durationMs ? this.currentCapture.durationMs / 1000 : 0);

      this.currentTimeEl.textContent = this.formatTime(cur);
      this.totalTimeEl.textContent = this.formatTime(dur);

      const addBadge = document.getElementById('marker-add-time-badge');
      if (addBadge) addBadge.textContent = this.formatTime(cur);

      if (dur > 0) {
        const pct = Math.min(100, Math.max(0, (cur / dur) * 100));
        this.timelineProgress.style.width = `${pct}%`;
      }
    }

    togglePlay() {
      if (!this.videoEl) return;
      if (this.videoEl.paused) this.videoEl.play();
      else this.videoEl.pause();
    }

    step(seconds) {
      if (!this.videoEl) return;
      this.videoEl.currentTime = Math.max(0, Math.min(this.videoEl.duration || 999999, this.videoEl.currentTime + seconds));
    }

    seekToMs(ms) {
      if (!this.videoEl) return;
      this.videoEl.currentTime = ms / 1000;
      this.videoEl.play().catch(() => {});
    }

    async openForCapture(capture) {
      this.initUI();
      this.currentCapture = capture;

      const titleEl = document.getElementById('marker-modal-capture-info');
      if (titleEl) {
        titleEl.textContent = `(ID: #${capture.id} · ${capture.patientName || 'İsimsiz'} · ${capture.roomName || ''})`;
      }

      this.videoEl.src = capture.filePath;
      this.videoEl.currentTime = 0;
      this.customLabelInput.value = '';

      const overlay = document.getElementById('marker-modal-overlay');
      if (overlay) overlay.classList.add('active');

      await this.loadMarkers();
    }

    async loadMarkers() {
      if (!this.currentCapture) return;
      try {
        const res = await fetch(`/api/captures/${this.currentCapture.id}/markers`);
        if (!res.ok) throw new Error('Markerlar yüklenemedi');
        this.markers = await res.json();
        this.renderMarkersList();
        this.renderTimelinePins();
      } catch (err) {
        console.error('Marker yükleme hatası:', err);
      }
    }

    renderTimelinePins() {
      if (!this.pinsContainer) return;
      this.pinsContainer.innerHTML = '';

      const dur = this.videoEl.duration || (this.currentCapture?.durationMs ? this.currentCapture.durationMs / 1000 : 0);
      if (!dur || dur <= 0) return;

      this.markers.forEach(m => {
        const pinSec = m.timestampMs / 1000;
        const pct = Math.min(100, Math.max(0, (pinSec / dur) * 100));

        const pin = document.createElement('div');
        pin.className = 'timeline-marker-pin';
        pin.style.left = `${pct}%`;
        pin.title = `${this.formatTime(pinSec)} - ${m.label}`;

        pin.addEventListener('click', (e) => {
          e.stopPropagation();
          this.seekToMs(m.timestampMs);
        });

        this.pinsContainer.appendChild(pin);
      });
    }

    renderMarkersList() {
      if (!this.markerListEl) return;
      this.markerListEl.innerHTML = '';

      const countBadge = document.getElementById('marker-count-badge');
      if (countBadge) countBadge.textContent = `${this.markers.length} İşaret`;

      if (this.markers.length === 0) {
        this.markerListEl.innerHTML = `
          <div style="font-size:11px;color:var(--muted,#94a3b8);text-align:center;padding:24px 0;">
            Henüz işaret eklenmedi.<br>Yukarıdaki etiketlere basarak anlık işaret ekleyebilirsiniz.
          </div>
        `;
        return;
      }

      this.markers.forEach(m => {
        const item = document.createElement('div');
        item.className = 'marker-list-item';
        item.innerHTML = `
          <span class="marker-item-time">${this.formatTime(m.timestampMs / 1000)}</span>
          <span class="marker-item-label">${m.label}</span>
          <button class="marker-item-del" title="İşareti Sil" onclick="event.stopPropagation(); VideoMarker.deleteMarker(${m.id})">
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
              <polyline points="3 6 5 6 21 6"></polyline>
              <path d="M19 6l-1 14H6L5 6"></path>
              <line x1="10" y1="11" x2="10" y2="17"></line>
              <line x1="14" y1="11" x2="14" y2="17"></line>
            </svg>
          </button>
        `;

        item.addEventListener('click', () => {
          this.seekToMs(m.timestampMs);
        });

        this.markerListEl.appendChild(item);
      });
    }

    async addMarkerWithLabel(label) {
      if (!this.currentCapture) return;
      const curSec = this.videoEl.currentTime || 0;
      const tsMs = Math.round(curSec * 1000);

      try {
        const res = await fetch(`/api/captures/${this.currentCapture.id}/markers`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ label, timestampMs: tsMs })
        });

        if (!res.ok) throw new Error('Marker eklenemedi');
        await this.loadMarkers();
      } catch (err) {
        alert('İşaret kaydedilemedi: ' + err.message);
      }
    }

    async addCustomMarker() {
      const val = this.customLabelInput.value.trim();
      if (!val) return;
      await this.addMarkerWithLabel(val);
      this.customLabelInput.value = '';
    }

    async deleteMarker(markerId) {
      if (!confirm('Bu işareti silmek istediğinize emin misiniz?')) return;
      try {
        const res = await fetch(`/api/markers/${markerId}`, { method: 'DELETE' });
        if (!res.ok) throw new Error('Silinemedi');
        await this.loadMarkers();
      } catch (err) {
        alert('Marker silme hatası: ' + err.message);
      }
    }

    close() {
      if (this.videoEl) {
        this.videoEl.pause();
        this.videoEl.src = '';
      }
      const overlay = document.getElementById('marker-modal-overlay');
      if (overlay) overlay.classList.remove('active');
    }
  }

  // Global singleton
  window.VideoMarker = new VideoMarkerManager();
})();
