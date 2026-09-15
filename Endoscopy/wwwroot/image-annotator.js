/**
 * MedicalImageAnnotator - Endoskopik Görüntü Çizim ve Ölçüm Motoru
 * Fotoğraflar üzerinde ok, daire, dikdörtgen, serbest çizim, metin ve milimetrik ölçüm cetveli (caliper) sağlar.
 */
(function() {
  class MedicalImageAnnotator {
    constructor() {
      this.currentCapture = null;
      this.onSavedCallback = null;
      this.currentTool = 'arrow'; // arrow | ruler | circle | rect | freehand | text
      this.currentColor = '#eab308'; // Neon Sarı (medikal yüksek kontrast)
      this.currentWidth = 3;
      this.pxPerMm = 12.0; // 12 piksel = 1 mm (varsayılan kalibrasyon)

      this.shapes = [];
      this.redoStack = [];
      this.isDrawing = false;
      this.currentShape = null;

      this.bgImage = new Image();
      this.bgImage.crossOrigin = 'anonymous';

      this.injected = false;
    }

    initUI() {
      if (this.injected) return;
      this.injected = true;

      const modalHtml = `
      <div id="annotator-modal-overlay" class="annotator-modal-overlay">
        <div class="annotator-container">
          
          <!-- Top Toolbar -->
          <div class="annotator-toolbar">
            
            <!-- Left Title Area -->
            <div class="annotator-title-area">
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#38bdf8" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <path d="M12 20h9"/>
                <path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z"/>
              </svg>
              <span id="ann-capture-info">Fotoğraf Çizim ve Ölçüm</span>
            </div>

            <!-- Center Tools Group -->
            <div class="annotator-tools-group">
              
              <!-- Ok (Arrow) -->
              <button class="annotator-tool-btn active" data-tool="arrow" title="Lezyon Gösterici Ok">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
                  <line x1="5" y1="19" x2="19" y2="5"/>
                  <polyline points="10 5 19 5 19 14"/>
                </svg>
                Ok
              </button>

              <!-- Cetvel / Caliper -->
              <button class="annotator-tool-btn" data-tool="ruler" title="Milimetrik Ölçüm Cetveli (Caliper)">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
                  <path d="M21.3 15.3l-6.6 6.6a2.4 2.4 0 0 1-3.4 0L2.7 13.3a2.4 2.4 0 0 1 0-3.4l6.6-6.6a2.4 2.4 0 0 1 3.4 0l8.6 8.6a2.4 2.4 0 0 1 0 3.4z"/>
                  <line x1="14" y1="7" x2="17" y2="10"/>
                  <line x1="10" y1="11" x2="13" y2="14"/>
                  <line x1="6" y1="15" x2="9" y2="18"/>
                </svg>
                Ölçüm (mm)
              </button>

              <!-- Daire (Circle) -->
              <button class="annotator-tool-btn" data-tool="circle" title="Polip / Lezyon Çevresi (Daire)">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
                  <circle cx="12" cy="12" r="9"/>
                </svg>
                Daire
              </button>

              <!-- Dikdörtgen (Rect) -->
              <button class="annotator-tool-btn" data-tool="rect" title="Bölge Kutusu (Dikdörtgen)">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
                  <rect x="3" y="3" width="18" height="18" rx="2"/>
                </svg>
                Kutu
              </button>

              <!-- Serbest Çizim (Freehand) -->
              <button class="annotator-tool-btn" data-tool="freehand" title="Serbest Kalem Çizimi">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
                  <path d="M4 20h4L18.5 9.5a2.12 2.12 0 0 0-3-3L5 17l-1 3z"/>
                </svg>
                Kalem
              </button>

              <!-- Metin (Text) -->
              <button class="annotator-tool-btn" data-tool="text" title="Fotoğraf Üzerine Not / Etiket">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
                  <polyline points="4 7 4 4 20 4 20 7"/>
                  <line x1="9" y1="20" x2="15" y2="20"/>
                  <line x1="12" y1="4" x2="12" y2="20"/>
                </svg>
                Metin
              </button>

              <div class="annotator-divider"></div>

              <!-- Color Palette -->
              <div class="annotator-color-picker">
                <div class="color-dot active" data-color="#eab308" style="background:#eab308" title="Neon Sarı"></div>
                <div class="color-dot" data-color="#ef4444" style="background:#ef4444" title="Sinyal Kırmızı"></div>
                <div class="color-dot" data-color="#06b6d4" style="background:#06b6d4" title="Turkuaz"></div>
                <div class="color-dot" data-color="#22c55e" style="background:#22c55e" title="Yeşil"></div>
                <div class="color-dot" data-color="#ffffff" style="background:#ffffff" title="Beyaz"></div>
              </div>

              <div class="annotator-divider"></div>

              <!-- Stroke Width -->
              <div class="annotator-width-picker">
                <span>Kalınlık:</span>
                <select id="ann-stroke-width">
                  <option value="2">2px (İnce)</option>
                  <option value="3" selected>3px (Normal)</option>
                  <option value="5">5px (Kalın)</option>
                </select>
              </div>

              <!-- Caliper Kalibrasyon Ayarı -->
              <div class="annotator-caliper-box" title="1 mm kaç piksel (varsayılan 12px)">
                <span>1 mm =</span>
                <input id="ann-px-per-mm" type="number" step="0.5" min="1" max="100" value="12" />
                <span>px</span>
              </div>

              <div class="annotator-divider"></div>

              <!-- Geri Al / Temizle -->
              <button class="annotator-tool-btn" id="btn-ann-undo" title="Geri Al (Ctrl+Z)">
                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
                  <polyline points="1 4 1 10 7 10"/>
                  <path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10"/>
                </svg>
                Geri
              </button>
              <button class="annotator-tool-btn" id="btn-ann-clear" title="Tüm Çizimleri Temizle">
                Temizle
              </button>

            </div>

            <!-- Right Actions Group -->
            <div class="annotator-actions">
              <button class="annotator-btn annotator-btn-primary" id="btn-ann-save">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
                  <path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z"/>
                  <polyline points="17 21 17 13 7 13 7 21"/>
                  <polyline points="7 3 7 8 15 8"/>
                </svg>
                İşaretli Kaydet
              </button>
              <button class="annotator-btn annotator-btn-ghost" id="btn-ann-download" title="Fotoğrafı İndir">
                İndir
              </button>
              <button class="annotator-btn annotator-btn-ghost" id="btn-ann-close">
                Kapat
              </button>
            </div>

          </div>

          <!-- Center Workspace -->
          <div class="annotator-workspace" id="annotator-workspace">
            <div class="annotator-canvas-wrapper" id="annotator-canvas-wrapper">
              <canvas id="annotator-canvas"></canvas>
            </div>
          </div>

          <!-- Bottom Status/Hint Bar -->
          <div class="annotator-bottom-bar">
            <div>
              <span>İpucu: Lezyon üzerine ok çekebilir, cetvel ile milimetrik çap ölçebilir veya metin etiketi bırakabilirsiniz.</span>
            </div>
            <div>
              <span>Orijinal fotoğraf daima korunur; çizim yeni bir kayıt olarak eklenir.</span>
            </div>
          </div>

        </div>
      </div>
      `;

      document.body.insertAdjacentHTML('beforeend', modalHtml);
      this.bindEvents();
    }

    bindEvents() {
      // Kapat butonları
      document.getElementById('btn-ann-close').addEventListener('click', () => this.close());
      document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && document.getElementById('annotator-modal-overlay').classList.contains('active')) {
          this.close();
        }
        if ((e.ctrlKey || e.metaKey) && e.key === 'z') {
          this.undo();
        }
      });

      // Araç butonları
      document.querySelectorAll('.annotator-tool-btn[data-tool]').forEach(btn => {
        btn.addEventListener('click', () => {
          document.querySelectorAll('.annotator-tool-btn[data-tool]').forEach(b => b.classList.remove('active'));
          btn.classList.add('active');
          this.currentTool = btn.getAttribute('data-tool');
        });
      });

      // Renk paleti
      document.querySelectorAll('.color-dot').forEach(dot => {
        dot.addEventListener('click', () => {
          document.querySelectorAll('.color-dot').forEach(d => d.classList.remove('active'));
          dot.classList.add('active');
          this.currentColor = dot.getAttribute('data-color');
        });
      });

      // Çizgi kalınlığı
      document.getElementById('ann-stroke-width').addEventListener('change', (e) => {
        this.currentWidth = parseInt(e.target.value, 10);
      });

      // Caliper kalibrasyon
      document.getElementById('ann-px-per-mm').addEventListener('change', (e) => {
        const val = parseFloat(e.target.value);
        if (val > 0) this.pxPerMm = val;
        this.redraw();
      });

      // Geri al & Temizle
      document.getElementById('btn-ann-undo').addEventListener('click', () => this.undo());
      document.getElementById('btn-ann-clear').addEventListener('click', () => this.clear());

      // Kaydet ve İndir
      document.getElementById('btn-ann-save').addEventListener('click', () => this.saveAnnotated());
      document.getElementById('btn-ann-download').addEventListener('click', () => this.downloadLocal());

      // Canvas çizim olayları
      const canvas = document.getElementById('annotator-canvas');
      canvas.addEventListener('mousedown', (e) => this.onMouseDown(e));
      window.addEventListener('mousemove', (e) => this.onMouseMove(e));
      window.addEventListener('mouseup', (e) => this.onMouseUp(e));
    }

    getCanvasCoords(e) {
      const canvas = document.getElementById('annotator-canvas');
      const rect = canvas.getBoundingClientRect();
      const scaleX = canvas.width / rect.width;
      const scaleY = canvas.height / rect.height;
      return {
        x: (e.clientX - rect.left) * scaleX,
        y: (e.clientY - rect.top) * scaleY
      };
    }

    onMouseDown(e) {
      if (!this.currentCapture) return;
      const canvas = document.getElementById('annotator-canvas');
      if (e.target !== canvas) return;

      const { x, y } = this.getCanvasCoords(e);
      this.isDrawing = true;

      if (this.currentTool === 'text') {
        const labelText = prompt('Fotoğraf üzerine eklenecek notu yazın:', '6 mm polip');
        if (labelText && labelText.trim()) {
          this.shapes.push({
            type: 'text',
            x: Math.round(x),
            y: Math.round(y),
            text: labelText.trim(),
            color: this.currentColor,
            fontSize: Math.max(14, Math.round(this.currentWidth * 4.5))
          });
          this.redoStack = [];
          this.redraw();
        }
        this.isDrawing = false;
        return;
      }

      this.currentShape = {
        type: this.currentTool,
        x1: x,
        y1: y,
        x2: x,
        y2: y,
        color: this.currentColor,
        width: this.currentWidth,
        points: this.currentTool === 'freehand' ? [{ x, y }] : []
      };
    }

    onMouseMove(e) {
      if (!this.isDrawing || !this.currentShape) return;
      const { x, y } = this.getCanvasCoords(e);

      if (this.currentShape.type === 'freehand') {
        this.currentShape.points.push({ x, y });
      } else {
        this.currentShape.x2 = x;
        this.currentShape.y2 = y;
      }

      this.redraw();
    }

    onMouseUp(e) {
      if (!this.isDrawing || !this.currentShape) return;
      this.isDrawing = false;

      // Min mesafe kontrolü (tıklayıp bırakılan boş çizimleri önle)
      if (this.currentShape.type === 'freehand') {
        if (this.currentShape.points.length > 2) {
          this.shapes.push(this.currentShape);
          this.redoStack = [];
        }
      } else {
        const dx = this.currentShape.x2 - this.currentShape.x1;
        const dy = this.currentShape.y2 - this.currentShape.y1;
        if (Math.hypot(dx, dy) > 5) {
          this.shapes.push(this.currentShape);
          this.redoStack = [];
        }
      }

      this.currentShape = null;
      this.redraw();
    }

    undo() {
      if (this.shapes.length > 0) {
        this.redoStack.push(this.shapes.pop());
        this.redraw();
      }
    }

    clear() {
      if (this.shapes.length === 0) return;
      if (confirm('Tüm çizim ve ölçümleri temizlemek istediğinize emin misiniz?')) {
        this.shapes = [];
        this.redoStack = [];
        this.redraw();
      }
    }

    redraw() {
      const canvas = document.getElementById('annotator-canvas');
      if (!canvas) return;
      const ctx = canvas.getContext('2d');

      ctx.clearRect(0, 0, canvas.width, canvas.height);
      if (this.bgImage.complete && this.bgImage.naturalWidth > 0) {
        ctx.drawImage(this.bgImage, 0, 0, canvas.width, canvas.height);
      }

      // Kayıtlı şekilleri çiz
      this.shapes.forEach(shape => this.renderShape(ctx, shape));

      // Anlık çizilmekte olan şekil
      if (this.currentShape) {
        this.renderShape(ctx, this.currentShape);
      }
    }

    renderShape(ctx, shape) {
      ctx.save();
      ctx.strokeStyle = shape.color;
      ctx.fillStyle = shape.color;
      ctx.lineWidth = shape.width;
      ctx.lineCap = 'round';
      ctx.lineJoin = 'round';

      switch (shape.type) {
        case 'arrow':
          this.drawArrow(ctx, shape.x1, shape.y1, shape.x2, shape.y2, shape.color, shape.width);
          break;
        case 'ruler':
          this.drawRuler(ctx, shape.x1, shape.y1, shape.x2, shape.y2, shape.color, shape.width);
          break;
        case 'circle':
          this.drawCircle(ctx, shape.x1, shape.y1, shape.x2, shape.y2);
          break;
        case 'rect':
          this.drawRect(ctx, shape.x1, shape.y1, shape.x2, shape.y2);
          break;
        case 'freehand':
          this.drawFreehand(ctx, shape.points);
          break;
        case 'text':
          this.drawText(ctx, shape.x, shape.y, shape.text, shape.color, shape.fontSize);
          break;
      }

      ctx.restore();
    }

    drawArrow(ctx, x1, y1, x2, y2, color, width) {
      const headLength = Math.max(14, width * 4.5);
      const angle = Math.atan2(y2 - y1, x2 - x1);

      // Gövde çizgisi
      ctx.beginPath();
      ctx.moveTo(x1, y1);
      ctx.lineTo(x2, y2);
      ctx.stroke();

      // Ok ucu (üçgen)
      ctx.beginPath();
      ctx.moveTo(x2, y2);
      ctx.lineTo(
        x2 - headLength * Math.cos(angle - Math.PI / 6),
        y2 - headLength * Math.sin(angle - Math.PI / 6)
      );
      ctx.lineTo(
        x2 - headLength * Math.cos(angle + Math.PI / 6),
        y2 - headLength * Math.sin(angle + Math.PI / 6)
      );
      ctx.closePath();
      ctx.fill();
    }

    drawRuler(ctx, x1, y1, x2, y2, color, width) {
      const dx = x2 - x1;
      const dy = y2 - y1;
      const distPx = Math.hypot(dx, dy);
      const distMm = (distPx / this.pxPerMm).toFixed(1);
      const angle = Math.atan2(dy, dx);
      const tickLength = Math.max(10, width * 3.5);

      // Ana çizgi
      ctx.beginPath();
      ctx.moveTo(x1, y1);
      ctx.lineTo(x2, y2);
      ctx.stroke();

      // Başlangıç çentiği (|)
      ctx.beginPath();
      ctx.moveTo(
        x1 + tickLength * Math.sin(angle),
        y1 - tickLength * Math.cos(angle)
      );
      ctx.lineTo(
        x1 - tickLength * Math.sin(angle),
        y1 + tickLength * Math.cos(angle)
      );
      ctx.stroke();

      // Bitiş çentiği (|)
      ctx.beginPath();
      ctx.moveTo(
        x2 + tickLength * Math.sin(angle),
        y2 - tickLength * Math.cos(angle)
      );
      ctx.lineTo(
        x2 - tickLength * Math.sin(angle),
        y2 + tickLength * Math.cos(angle)
      );
      ctx.stroke();

      // Ölçüm Rozeti (Metin Kutusu)
      const midX = (x1 + x2) / 2;
      const midY = (y1 + y2) / 2;
      const label = `${distMm} mm`;

      ctx.font = 'bold 13px Segoe UI, sans-serif';
      const textMetrics = ctx.measureText(label);
      const padX = 6;
      const padY = 3;
      const rectW = textMetrics.width + padX * 2;
      const rectH = 20;

      ctx.save();
      ctx.fillStyle = 'rgba(15, 23, 42, 0.85)';
      ctx.strokeStyle = color;
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.roundRect(midX - rectW / 2, midY - rectH / 2, rectW, rectH, 4);
      ctx.fill();
      ctx.stroke();

      ctx.fillStyle = '#ffffff';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(label, midX, midY);
      ctx.restore();
    }

    drawCircle(ctx, x1, y1, x2, y2) {
      const rx = Math.abs(x2 - x1) / 2;
      const ry = Math.abs(y2 - y1) / 2;
      const cx = (x1 + x2) / 2;
      const cy = (y1 + y2) / 2;

      ctx.beginPath();
      ctx.ellipse(cx, cy, Math.max(rx, 1), Math.max(ry, 1), 0, 0, 2 * Math.PI);
      ctx.stroke();
    }

    drawRect(ctx, x1, y1, x2, y2) {
      const x = Math.min(x1, x2);
      const y = Math.min(y1, y2);
      const w = Math.abs(x2 - x1);
      const h = Math.abs(y2 - y1);

      ctx.beginPath();
      ctx.strokeRect(x, y, w, h);
    }

    drawFreehand(ctx, points) {
      if (points.length < 2) return;
      ctx.beginPath();
      ctx.moveTo(points[0].x, points[0].y);
      for (let i = 1; i < points.length; i++) {
        ctx.lineTo(points[i].x, points[i].y);
      }
      ctx.stroke();
    }

    drawText(ctx, x, y, text, color, fontSize) {
      ctx.save();
      ctx.font = `bold ${fontSize || 14}px Segoe UI, sans-serif`;
      const metrics = ctx.measureText(text);
      const padX = 8;
      const padY = 4;
      const boxW = metrics.width + padX * 2;
      const boxH = (fontSize || 14) + padY * 2;

      ctx.fillStyle = 'rgba(15, 23, 42, 0.85)';
      ctx.strokeStyle = color;
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.roundRect(x, y - boxH, boxW, boxH, 4);
      ctx.fill();
      ctx.stroke();

      ctx.fillStyle = '#ffffff';
      ctx.textBaseline = 'bottom';
      ctx.fillText(text, x + padX, y - padY);
      ctx.restore();
    }

    /**
     * Çizim Modalı Açma
     * @param {Object} captureItem - { id, filePath, patientName, patientIdentifier, doctorName, procedureType, ... }
     * @param {Function} onSavedCallback - Başarıyla kaydedildiğinde çağrılacak fonksiyon
     */
    open(captureItem, onSavedCallback = null) {
      this.initUI();

      this.currentCapture = captureItem;
      this.onSavedCallback = onSavedCallback;
      this.shapes = [];
      this.redoStack = [];

      const infoText = `Id: #${captureItem.id} · ${captureItem.patientName || 'İsimsiz'} (${captureItem.procedureType || 'Fotoğraf'})`;
      document.getElementById('ann-capture-info').textContent = infoText;

      const canvas = document.getElementById('annotator-canvas');
      const overlay = document.getElementById('annotator-modal-overlay');

      this.bgImage.onload = () => {
        canvas.width = this.bgImage.naturalWidth || 1280;
        canvas.height = this.bgImage.naturalHeight || 720;
        this.redraw();
      };

      // Cache busting ile resmi yükle
      this.bgImage.src = captureItem.filePath + '?t=' + Date.now();

      overlay.classList.add('active');
    }

    async saveAnnotated() {
      if (!this.currentCapture) return;
      const btn = document.getElementById('btn-ann-save');
      const originalText = btn.innerHTML;

      btn.disabled = true;
      btn.innerHTML = 'Kaydediliyor…';

      const canvas = document.getElementById('annotator-canvas');
      const imageBase64 = canvas.toDataURL('image/jpeg', 0.95);

      try {
        const res = await fetch(`/api/captures/${this.currentCapture.id}/annotate`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            imageBase64: imageBase64,
            label: 'Çizim ve Ölçüm'
          })
        });

        if (!res.ok) {
          const err = await res.json().catch(() => ({}));
          alert('Kaydetme hatası: ' + (err.message || res.statusText));
          btn.disabled = false;
          btn.innerHTML = originalText;
          return;
        }

        const savedData = await res.json();
        btn.disabled = false;
        btn.innerHTML = originalText;

        this.close();

        if (typeof this.onSavedCallback === 'function') {
          this.onSavedCallback(savedData);
        }
      } catch (e) {
        alert('Bağlantı hatası: ' + e.message);
        btn.disabled = false;
        btn.innerHTML = originalText;
      }
    }

    downloadLocal() {
      const canvas = document.getElementById('annotator-canvas');
      const link = document.createElement('a');
      link.download = `Annotated_Capture_${this.currentCapture ? this.currentCapture.id : 'image'}.jpg`;
      link.href = canvas.toDataURL('image/jpeg', 0.95);
      link.click();
    }

    close() {
      const overlay = document.getElementById('annotator-modal-overlay');
      if (overlay) {
        overlay.classList.remove('active');
      }
      this.currentCapture = null;
    }
  }

  window.MedicalAnnotator = new MedicalImageAnnotator();
})();
