/**
 * MedicalReportGenerator - Endoskopi & Kolonoskopi Tıbbi Rapor Motoru
 * Tek tıkla resmi A4 standartlarında medikal PDF raporu üretir ve yazdırır.
 */
(function() {
  // Klinik Hızlı Şablonlar
  const CLINICAL_TEMPLATES = {
    normal_gastro: {
      label: 'Normal Gastroskopi',
      procedure: 'Tanısal Özofagogastroduodenoskopi (Gastroskopi)',
      indication: 'Dispeptik yakınmalar, epigastrik dolgunluk.',
      findings: 'Özofagus lümeni, motilitesi ve mukozası normal olarak izlendi. Z çizgisi 40. cm\'de düzenli geçildi, hiatal herni saptanmadı.\n' +
                'Mide kardia, fundus, korpus ve antrum mukozası doğal pembe renkte, lezyon, erozyon veya aktif kanama odağı izlenmedi. İntraselüler sekresyon berrak.\n' +
                'Pilor santralize, yuvarlak ve regüler, geçiş serbest. Duodenum bulbus ve II. kıta mukozası ile plika yapıları normal.',
      diagnosis: 'Normal Endoskopik Bulgular (Patolojik lezyon saptanmadı).',
      recommendations: 'Diyet düzenlemesi ve semptomatik klinik takip önerildi.'
    },
    antral_gastritis: {
      label: 'Eritematöz Antral Gastrit',
      procedure: 'Tanısal Gastroskopi + Mide Mukozal Biyopsisi',
      indication: 'Kronik mide ağrısı, yanma, reflü şikayeti.',
      findings: 'Özofagus lümeni ve mukozası tabi. Z çizgisi düzenli.\n' +
                'Mide fundus ve korpus mukozasında hafif hiperemi mevcut. Antrum mukozasında yama tarzında eritematöz hiperemi alanları ve birkaç adet milimetrik süperfisiyal erozyon odakları izlendi. H. Pylori araştırması ve histopatolojik inceleme amacıyla antrum ve korpustan biyopsi örnekleri alındı.\n' +
                'Pilor düzenli, bulbus ve duodenum II. kıta normal.',
      diagnosis: 'Eritematöz ve Eroziv Antral Gastrit.\n(H. Pylori ve histopatolojik tanı için biyopsi alındı).',
      recommendations: 'Patoloji biyopsi sonucu beklenmektedir. Proton pompa inhibitörü (PPI) tedavisi başlandı; baharatlı, asitli gıdalardan kaçınması önerildi.'
    },
    normal_colono: {
      label: 'Normal Kolonoskopi',
      procedure: 'Total Kolonoskopi',
      indication: 'Tarama amaçlı kolonoskopi / Ailede polip öyküsü.',
      findings: 'Çekuma ve terminal ileuma kadar ulaşıldı (Bağırsak temizliği Boston Skoru: 8/9, mükemmel).\n' +
                'Terminal ileum lümeni ve villöz yapısı normal.\n' +
                'Çekum, çıkan kolon, hepatik fleksura, transvers kolon, splenik fleksura, inen kolon, sigmoid kolon ve rektum mukozası normal damarsal yapıda; polip, kitle, divertikül veya inflamatuar lezyon saptanmadı.',
      diagnosis: 'Normal Total Kolonoskopik İnceleme.',
      recommendations: 'Rutin tarama protokolü kapsamında 5-10 yıl sonra kontrol kolonoskopisi planlanması önerilir.'
    },
    polyps_colono: {
      label: 'Kolon Polibi & Polipektomi',
      procedure: 'Total Kolonoskopi + Soğuk Snare Polipektomi',
      indication: 'Gaitada gizli kan pozitifliği / Kolorektal tarama.',
      findings: 'Çekuma ulaşıldı, apendiks orifisi ve ileoçekal valv görüldü.\n' +
                'Sigmoid kolonda lümen içerisinde yaklaşık 6 mm çapında sesil (saplı olmayan) polip izlendi. Lezyon soğuk snare (cold snare) yöntemi ile tam olarak rezeke edildi (polipektomi). İşlem alanında kanama izlenmedi, biyopsi örneği histopatolojik inceleme için patolojiye gönderildi.\n' +
                'Diğer kolon segmentleri ve rektum mukozası normal.',
      diagnosis: 'Sigmoid Kolon Polibi (Tam endoskopik polipektomi uygulandı).',
      recommendations: 'Rezeke edilen polipin patoloji sonucu ile değerlendirilmesi ve patoloji sonucuna göre 3-5 yıl sonra kontrol kolonoskopisi önerildi.'
    }
  };

  class MedicalReportEngine {
    constructor() {
      this.currentData = null;
      this.availableCaptures = [];
      this.selectedCaptures = [];
      this.injected = false;
    }

    initUI() {
      if (this.injected) return;
      this.injected = true;

      const modalHtml = `
      <div id="report-modal-overlay" class="report-modal-overlay">
        <div class="report-modal">
          <!-- Modal Header -->
          <div class="report-modal-header">
            <div class="report-modal-title">
              <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="#38bdf8" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
                <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
                <polyline points="14 2 14 8 20 8"/>
                <line x1="16" y1="13" x2="8" y2="13"/>
                <line x1="16" y1="17" x2="8" y2="17"/>
                <polyline points="10 9 9 9 8 9"/>
              </svg>
              <span>Medikal Rapor Oluşturucu</span>
              <span class="badge">A4 Tıbbi Format</span>
            </div>
            <div class="report-modal-actions">
              <button class="report-btn report-btn-success" id="btn-download-pdf">
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
                  <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/>
                  <polyline points="7 10 12 15 17 10"/>
                  <line x1="12" y1="15" x2="12" y2="3"/>
                </svg>
                PDF İndir
              </button>
              <button class="report-btn report-btn-primary" id="btn-print-report">
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
                  <polyline points="6 9 6 2 18 2 18 9"/>
                  <path d="M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2"/>
                  <rect x="6" y="14" width="12" height="8"/>
                </svg>
                Yazdır / A4
              </button>
              <button class="report-btn report-btn-ghost" id="btn-close-report">
                Kapat
              </button>
            </div>
          </div>

          <!-- Modal Body -->
          <div class="report-modal-body">
            
            <!-- Left: Form Editor -->
            <div class="report-editor-col">

              <!-- Kurum & Antet -->
              <div class="report-section-box">
                <h4>Kurum ve Rapor Bilgisi</h4>
                <div class="report-field-grid">
                  <div class="report-field full-width">
                    <label>Hastane / Klinik Başlığı</label>
                    <input id="rep-inp-institution" type="text" value="ÖZEL ENDOSKOPİ & GASTROENTEROLOJİ MERKEZİ" />
                  </div>
                  <div class="report-field full-width">
                    <label>Birim / Servis</label>
                    <input id="rep-inp-subtitle" type="text" value="Tıbbi Görüntüleme ve Girişimsel Endoskopi Ünitesi" />
                  </div>
                  <div class="report-field">
                    <label>Rapor Protokol No</label>
                    <input id="rep-inp-report-no" type="text" />
                  </div>
                  <div class="report-field">
                    <label>Rapor Tarihi</label>
                    <input id="rep-inp-report-date" type="text" />
                  </div>
                </div>
              </div>

              <!-- Hasta ve Hekim Bilgileri -->
              <div class="report-section-box">
                <h4>Hasta ve Hekim Bilgileri</h4>
                <div class="report-field-grid">
                  <div class="report-field">
                    <label>Hasta Adı Soyadı</label>
                    <input id="rep-inp-patient-name" type="text" placeholder="örn. Ahmet Yılmaz" />
                  </div>
                  <div class="report-field">
                    <label>Hasta No / MRN</label>
                    <input id="rep-inp-patient-id" type="text" placeholder="örn. 10293847" />
                  </div>
                  <div class="report-field">
                    <label>Yaş / Cinsiyet</label>
                    <input id="rep-inp-patient-meta" type="text" placeholder="örn. 48 / Erkek" />
                  </div>
                  <div class="report-field">
                    <label>İşlem Türü</label>
                    <input id="rep-inp-procedure" type="text" placeholder="örn. Gastroskopi" />
                  </div>
                  <div class="report-field">
                    <label>Sorumlu Doktor</label>
                    <input id="rep-inp-doctor" type="text" placeholder="örn. Dr. Ayşe Kaya" />
                  </div>
                  <div class="report-field">
                    <label>Kaydı Oluşturan / Oda</label>
                    <input id="rep-inp-creator" type="text" placeholder="örn. Hemşire Zeynep (oda1)" />
                  </div>
                </div>
              </div>

              <!-- Hızlı Şablonlar -->
              <div class="report-section-box">
                <h4>Hızlı Medikal Şablonlar</h4>
                <div style="margin-bottom: 6px;">
                  <span class="template-badge" data-key="normal_gastro">Normal Gastroskopi</span>
                  <span class="template-badge" data-key="antral_gastritis">Antral Gastrit</span>
                  <span class="template-badge" data-key="normal_colono">Normal Kolonoskopi</span>
                  <span class="template-badge" data-key="polyps_colono">Kolon Polibi / Rezeksiyon</span>
                </div>
              </div>

              <!-- Bulgular, Tanı ve Öneriler -->
              <div class="report-section-box">
                <h4>Bulgular, Tanı ve Tedavi</h4>
                <div class="report-field full-width" style="margin-bottom: 8px;">
                  <label>Endikasyon / Ön Tanı</label>
                  <input id="rep-inp-indication" type="text" placeholder="İşlem nedeni ve ön tanı..." />
                </div>
                <div class="report-field full-width" style="margin-bottom: 8px;">
                  <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:4px;">
                    <label style="margin:0;">Endoskopik Bulgular</label>
                    <button type="button" class="btn btn-ghost btn-sm" id="btn-import-video-markers" onclick="MedicalReport.importVideoMarkersToFindings()" style="font-size:11px;padding:2px 8px;display:inline-flex;align-items:center;gap:4px;color:#f59e0b;" title="Bu hastaya/odaya ait videolardaki zaman damgalı işaretleri bulgulara ekle">
                      <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M19 21l-7-5-7 5V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2z"/></svg>
                      Video İşaretlerini Aktar
                    </button>
                  </div>
                  <textarea id="rep-inp-findings" rows="4" placeholder="Organ ve mukoza bulguları..."></textarea>
                </div>
                <div class="report-field full-width" style="margin-bottom: 8px;">
                  <label>Sonuç ve Tanı (Kesin Tanı)</label>
                  <textarea id="rep-inp-diagnosis" rows="2" placeholder="Kesin endoskopik tanı..."></textarea>
                </div>
                <div class="report-field full-width">
                  <label>Öneri ve Tedavi Planı</label>
                  <textarea id="rep-inp-recommendations" rows="2" placeholder="Önerilen takip, diyet veya ilaçlar..."></textarea>
                </div>
              </div>

              <!-- Fotoğraf Seçici -->
              <div class="report-section-box">
                <h4>Rapora Eklenecek Fotoğraflar (<span id="rep-selected-count">0</span>/6 Seçili)</h4>
                <p style="font-size: 11px; color: #94a3b8; margin: 0 0 6px 0;">
                  Raporda en net ve düzenli baskı için en fazla 6 fotoğraf seçilebilir.
                </p>
                <div class="report-img-picker-grid" id="rep-img-picker-grid">
                  <!-- JS ile fotoğraflar buraya enjekte edilir -->
                </div>
                <div class="report-img-captions" id="rep-img-captions">
                  <!-- Seçili fotoğrafların anatomik açıklamaları -->
                </div>
              </div>

            </div>

            <!-- Right: Live A4 Preview Sheet -->
            <div class="report-preview-col">
              <div class="medical-report-sheet" id="medical-report-sheet">
                
                <!-- Üst Kısım: Antet, Kurum ve Meta -->
                <div>
                  <div class="sheet-header">
                    <div class="sheet-logo-area">
                      <div class="sheet-cross-icon">+</div>
                      <div>
                        <div class="sheet-institution-title" id="sheet-preview-institution">ÖZEL ENDOSKOPİ & GASTROENTEROLOJİ MERKEZİ</div>
                        <div class="sheet-unit-subtitle">ENDOSKOPİ VE GASTROENTEROLOJİ RAPORU</div>
                        <div class="sheet-unit-contact" id="sheet-preview-subtitle">Tıbbi Görüntüleme ve Girişimsel Endoskopi Ünitesi</div>
                      </div>
                    </div>
                    <div class="sheet-meta-area">
                      <div class="sheet-report-badge" id="sheet-preview-report-no">RPR-2026-001</div>
                      <div id="sheet-preview-date">Tarih: 15.09.2026 13:30</div>
                    </div>
                  </div>

                  <!-- Hasta Demografik Tablosu -->
                  <table class="sheet-patient-table">
                    <tr>
                      <td class="lbl">Hasta Adı Soyadı:</td>
                      <td class="val" id="sheet-preview-patient-name">—</td>
                      <td class="lbl">Protokol / MRN:</td>
                      <td class="val" id="sheet-preview-patient-id">—</td>
                    </tr>
                    <tr>
                      <td class="lbl">Yaş / Cinsiyet:</td>
                      <td class="val" id="sheet-preview-patient-meta">—</td>
                      <td class="lbl">İşlem Tarihi:</td>
                      <td class="val" id="sheet-preview-patient-date">—</td>
                    </tr>
                    <tr>
                      <td class="lbl">Uygulanan İşlem:</td>
                      <td class="val" id="sheet-preview-procedure" style="color:#0284c7;font-weight:700;">—</td>
                      <td class="lbl">Sorumlu Hekim:</td>
                      <td class="val" id="sheet-preview-doctor">—</td>
                    </tr>
                    <tr>
                      <td class="lbl">Oluşturan / İstasyon:</td>
                      <td class="val" id="sheet-preview-creator" colspan="3">—</td>
                    </tr>
                  </table>

                  <!-- Endoskopik Görüntüler -->
                  <div id="sheet-images-container">
                    <div class="sheet-section-title">
                      <span>ENDOSKOPİK İNCELEME GÖRÜNTÜLERİ</span>
                    </div>
                    <div class="sheet-images-grid grid-4" id="sheet-images-grid">
                      <!-- Resimler JS ile buraya gelecek -->
                    </div>
                  </div>

                  <!-- Endikasyon -->
                  <div id="sheet-indication-wrap">
                    <div class="sheet-section-title">
                      <span>ENDİKASYON VE ÖN TANI</span>
                    </div>
                    <div class="sheet-text-block" id="sheet-preview-indication">—</div>
                  </div>

                  <!-- Bulgular -->
                  <div>
                    <div class="sheet-section-title">
                      <span>ENDOSKOPİK BULGULAR</span>
                    </div>
                    <div class="sheet-text-block" id="sheet-preview-findings">—</div>
                  </div>

                  <!-- Tanı / Sonuç Kutusu -->
                  <div class="sheet-diagnosis-box">
                    <div class="box-title">TANI VE SONUÇ</div>
                    <div class="box-content" id="sheet-preview-diagnosis">—</div>
                  </div>

                  <!-- Öneriler -->
                  <div id="sheet-rec-wrap">
                    <div class="sheet-section-title">
                      <span>ÖNERİ VE TEDAVİ PLANI</span>
                    </div>
                    <div class="sheet-text-block" id="sheet-preview-recommendations">—</div>
                  </div>
                </div>

                <!-- Alt Kısım: İmza ve Kaşe -->
                <div class="sheet-footer">
                  <div class="sheet-disclaimer">
                    İşbu rapor bilgisayar ortamında tıbbi standartlara uygun olarak düzenlenmiş olup, hastaya uygulanan endoskopik tetkikin resmi bulgularını ihtiva eder.
                  </div>
                  <div class="sheet-signature-box">
                    <div class="sheet-sig-doctor" id="sheet-preview-sig-doctor">Dr. —</div>
                    <div class="sheet-sig-title">Gastroenteroloji Uzmanı</div>
                    <div class="sheet-sig-line">İmza / Kaşe</div>
                  </div>
                </div>

              </div>
            </div>

          </div>
        </div>
      </div>
      `;

      document.body.insertAdjacentHTML('beforeend', modalHtml);
      this.bindEvents();
    }

    bindEvents() {
      // Kapat butonu
      document.getElementById('btn-close-report').addEventListener('click', () => this.close());
      document.getElementById('report-modal-overlay').addEventListener('click', (e) => {
        if (e.target.id === 'report-modal-overlay') this.close();
      });

      // ESC tuşuyla kapatma
      document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && document.getElementById('report-modal-overlay').classList.contains('active')) {
          this.close();
        }
      });

      // Form girdilerini canlı önizlemeye bağla
      const syncMap = [
        ['rep-inp-institution', 'sheet-preview-institution'],
        ['rep-inp-subtitle', 'sheet-preview-subtitle'],
        ['rep-inp-report-no', 'sheet-preview-report-no'],
        ['rep-inp-patient-name', 'sheet-preview-patient-name'],
        ['rep-inp-patient-id', 'sheet-preview-patient-id'],
        ['rep-inp-patient-meta', 'sheet-preview-patient-meta'],
        ['rep-inp-procedure', 'sheet-preview-procedure'],
        ['rep-inp-doctor', 'sheet-preview-doctor', (val) => {
          document.getElementById('sheet-preview-sig-doctor').textContent = val || 'Dr. —';
          return val;
        }],
        ['rep-inp-creator', 'sheet-preview-creator'],
        ['rep-inp-indication', 'sheet-preview-indication'],
        ['rep-inp-findings', 'sheet-preview-findings'],
        ['rep-inp-diagnosis', 'sheet-preview-diagnosis'],
        ['rep-inp-recommendations', 'sheet-preview-recommendations']
      ];

      syncMap.forEach(([inpId, previewId, customFn]) => {
        const inp = document.getElementById(inpId);
        if (!inp) return;
        const update = () => {
          const val = inp.value.trim();
          const target = document.getElementById(previewId);
          if (target) {
            target.textContent = customFn ? customFn(val) : (val || '—');
          }
          if (inpId === 'rep-inp-indication') {
            const wrap = document.getElementById('sheet-indication-wrap');
            if (wrap) wrap.style.display = val ? 'block' : 'none';
          }
          if (inpId === 'rep-inp-recommendations') {
            const wrap = document.getElementById('sheet-rec-wrap');
            if (wrap) wrap.style.display = val ? 'block' : 'none';
          }
        };
        inp.addEventListener('input', update);
      });

      // Hızlı Şablon Butonları
      document.querySelectorAll('.template-badge').forEach(badge => {
        badge.addEventListener('click', () => {
          const key = badge.getAttribute('data-key');
          const tpl = CLINICAL_TEMPLATES[key];
          if (!tpl) return;

          if (tpl.procedure) {
            document.getElementById('rep-inp-procedure').value = tpl.procedure;
            document.getElementById('sheet-preview-procedure').textContent = tpl.procedure;
          }
          if (tpl.indication) {
            document.getElementById('rep-inp-indication').value = tpl.indication;
            document.getElementById('sheet-preview-indication').textContent = tpl.indication;
            const wrap = document.getElementById('sheet-indication-wrap');
            if (wrap) wrap.style.display = 'block';
          }
          if (tpl.findings) {
            document.getElementById('rep-inp-findings').value = tpl.findings;
            document.getElementById('sheet-preview-findings').textContent = tpl.findings;
          }
          if (tpl.diagnosis) {
            document.getElementById('rep-inp-diagnosis').value = tpl.diagnosis;
            document.getElementById('sheet-preview-diagnosis').textContent = tpl.diagnosis;
          }
          if (tpl.recommendations) {
            document.getElementById('rep-inp-recommendations').value = tpl.recommendations;
            document.getElementById('sheet-preview-recommendations').textContent = tpl.recommendations;
            const wrap = document.getElementById('sheet-rec-wrap');
            if (wrap) wrap.style.display = 'block';
          }
        });
      });

      // PDF İndirme Butonu
      document.getElementById('btn-download-pdf').addEventListener('click', () => this.downloadPdf());

      // Yazdırma Butonu
      document.getElementById('btn-print-report').addEventListener('click', () => this.printReport());
    }

    /**
     * Rapor Modalı Açılış Fonksiyonu
     * @param {Object} options
     *   - patientIdentifier, patientName, doctorName, procedureType, createdByName, roomName
     *   - captures: Array of photo items [{ filePath, width, height, ... }]
     */
    open(options = {}) {
      this.initUI();

      this.currentData = options;
      const now = new Date();
      const reportDateStr = now.toLocaleDateString('tr-TR', { year: 'numeric', month: '2-digit', day: '2-digit' }) +
                            ' ' + now.toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' });
      const reportNoStr = `RPR-${now.getFullYear()}${String(now.getMonth()+1).padStart(2,'0')}${String(now.getDate()).padStart(2,'0')}-${Math.floor(1000 + Math.random() * 9000)}`;

      // Form Alanlarını Doldur
      document.getElementById('rep-inp-report-no').value = reportNoStr;
      document.getElementById('sheet-preview-report-no').textContent = reportNoStr;

      document.getElementById('rep-inp-report-date').value = reportDateStr;
      document.getElementById('sheet-preview-date').textContent = `Tarih: ${reportDateStr}`;
      document.getElementById('sheet-preview-patient-date').textContent = reportDateStr;

      const pName = options.patientName || '';
      document.getElementById('rep-inp-patient-name').value = pName;
      document.getElementById('sheet-preview-patient-name').textContent = pName || '—';

      const pId = options.patientIdentifier || '';
      document.getElementById('rep-inp-patient-id').value = pId;
      document.getElementById('sheet-preview-patient-id').textContent = pId || '—';

      const doctor = options.doctorName || '';
      document.getElementById('rep-inp-doctor').value = doctor;
      document.getElementById('sheet-preview-doctor').textContent = doctor || '—';
      document.getElementById('sheet-preview-sig-doctor').textContent = doctor ? (doctor.startsWith('Dr') ? doctor : `Dr. ${doctor}`) : 'Dr. —';

      const proc = options.procedureType || 'Endoskopik İnceleme';
      document.getElementById('rep-inp-procedure').value = proc;
      document.getElementById('sheet-preview-procedure').textContent = proc;

      const creator = (options.createdByName || '') + (options.roomName ? ` (${options.roomName})` : '');
      document.getElementById('rep-inp-creator').value = creator;
      document.getElementById('sheet-preview-creator').textContent = creator || '—';

      // Varsayılan boş veya mevcut değerler
      document.getElementById('rep-inp-patient-meta').value = options.patientMeta || '';
      document.getElementById('sheet-preview-patient-meta').textContent = options.patientMeta || '—';

      const indVal = options.indication || '';
      document.getElementById('rep-inp-indication').value = indVal;
      document.getElementById('sheet-preview-indication').textContent = indVal || '—';
      const indWrap = document.getElementById('sheet-indication-wrap');
      if (indWrap) indWrap.style.display = indVal ? 'block' : 'none';

      document.getElementById('rep-inp-findings').value = options.findings || '';
      document.getElementById('sheet-preview-findings').textContent = options.findings || '—';

      document.getElementById('rep-inp-diagnosis').value = options.diagnosis || '';
      document.getElementById('sheet-preview-diagnosis').textContent = options.diagnosis || '—';

      const recVal = options.recommendations || '';
      document.getElementById('rep-inp-recommendations').value = recVal;
      document.getElementById('sheet-preview-recommendations').textContent = recVal || '—';
      const recWrap = document.getElementById('sheet-rec-wrap');
      if (recWrap) recWrap.style.display = recVal ? 'block' : 'none';

      // Fotoğrafları hazırla (sadece photo türündekiler)
      let photoList = (options.captures || []).filter(c => c.captureType === 'photo' || (c.filePath && c.filePath.match(/\.(jpg|jpeg|png)$/i)));
      
      // Eğer hiç fotoğraf verilmediyse ve spesifik bir hasta belirtilmemişse sayfadaki mevcut tablodan çekmeyi dene
      const hasSpecificPatient = Boolean((options.patientIdentifier || '').trim() || (options.patientName || '').trim());
      if (photoList.length === 0 && !hasSpecificPatient && window.capturesData) {
        photoList = window.capturesData.filter(c => c.captureType === 'photo');
      }

      this.availableCaptures = photoList;
      // Varsayılan olarak en son 4 fotoğrafı (veya 6'ya kadar olanı) seçili yap
      this.selectedCaptures = photoList.slice(0, 4).map((c, i) => ({
        ...c,
        caption: `Şekil ${i+1}: Endoskopik Görünüm`
      }));

      this.renderImagePickers();
      this.updateSheetImages();

      // Modalı göster
      const overlay = document.getElementById('report-modal-overlay');
      overlay.classList.add('active');
    }

    renderImagePickers() {
      const grid = document.getElementById('rep-img-picker-grid');
      const captionsDiv = document.getElementById('rep-img-captions');
      grid.innerHTML = '';
      captionsDiv.innerHTML = '';

      if (this.availableCaptures.length === 0) {
        grid.innerHTML = '<div style="grid-column:1/-1;color:#64748b;font-size:11px;padding:10px;text-align:center;">Bu seans için çekilmiş fotoğraf bulunamadı.</div>';
        document.getElementById('rep-selected-count').textContent = '0';
        return;
      }

      this.availableCaptures.forEach((item, idx) => {
        const isSelected = this.selectedCaptures.some(sc => sc.id === item.id || sc.filePath === item.filePath);
        const itemEl = document.createElement('div');
        itemEl.className = `report-img-item ${isSelected ? 'selected' : ''}`;
        itemEl.innerHTML = `
          <img src="${item.filePath}" alt="Capture ${item.id}" />
          <div class="img-check">${isSelected ? '✓' : ''}</div>
        `;

        itemEl.addEventListener('click', () => {
          const currentlySelected = this.selectedCaptures.some(sc => sc.id === item.id || sc.filePath === item.filePath);
          if (currentlySelected) {
            this.selectedCaptures = this.selectedCaptures.filter(sc => sc.id !== item.id && sc.filePath !== item.filePath);
          } else {
            if (this.selectedCaptures.length >= 6) {
              alert('En fazla 6 adet fotoğraf seçebilirsiniz.');
              return;
            }
            this.selectedCaptures.push({
              ...item,
              caption: `Şekil ${this.selectedCaptures.length + 1}: Endoskopik Görünüm`
            });
          }
          this.renderImagePickers();
          this.updateSheetImages();
        });

        grid.appendChild(itemEl);
      });

      document.getElementById('rep-selected-count').textContent = this.selectedCaptures.length;

      // Açıklama alanları
      this.selectedCaptures.forEach((sc, i) => {
        const row = document.createElement('div');
        row.className = 'caption-row';
        row.innerHTML = `
          <img src="${sc.filePath}" />
          <input type="text" value="${sc.caption || `Şekil ${i+1}`}" placeholder="Fotoğraf açıklaması..." />
        `;
        const inp = row.querySelector('input');
        inp.addEventListener('input', () => {
          sc.caption = inp.value;
          this.updateSheetImages();
        });
        captionsDiv.appendChild(row);
      });
    }

    updateSheetImages() {
      const grid = document.getElementById('sheet-images-grid');
      const container = document.getElementById('sheet-images-container');
      grid.innerHTML = '';

      const count = this.selectedCaptures.length;
      if (count === 0) {
        container.style.display = 'none';
        return;
      }

      container.style.display = 'block';

      // Grid sınıfını belirle
      grid.className = 'sheet-images-grid';
      if (count === 1) grid.classList.add('grid-1');
      else if (count === 2) grid.classList.add('grid-2');
      else if (count === 3) grid.classList.add('grid-3');
      else if (count <= 4) grid.classList.add('grid-4');
      else grid.classList.add('grid-6');

      this.selectedCaptures.forEach((item, index) => {
        const card = document.createElement('div');
        card.className = 'sheet-img-card';
        card.innerHTML = `
          <img src="${item.filePath}" alt="Endoskopik Fotoğraf" />
          <div class="sheet-img-caption">${item.caption || `Şekil ${index+1}`}</div>
        `;
        grid.appendChild(card);
      });
    }

    downloadPdf() {
      const sheet = document.getElementById('medical-report-sheet');
      const btn = document.getElementById('btn-download-pdf');
      const originalText = btn.innerHTML;

      btn.disabled = true;
      btn.innerHTML = 'PDF Hazırlanıyor…';

      const pName = (document.getElementById('rep-inp-patient-name').value.trim() || 'Hasta').replace(/[^a-zA-Z0-9_-]/g, '_');
      const repNo = (document.getElementById('rep-inp-report-no').value.trim() || 'Rapor').replace(/[^a-zA-Z0-9_-]/g, '_');
      const fileName = `Endoskopi_Raporu_${pName}_${repNo}.pdf`;

      if (typeof window.html2pdf === 'function') {
        const opt = {
          margin: 0,
          filename: fileName,
          image: { type: 'jpeg', quality: 0.98 },
          html2canvas: {
            scale: 2,
            useCORS: true,
            logging: false,
            letterRendering: true
          },
          jsPDF: { unit: 'mm', format: 'a4', orientation: 'portrait' },
          pagebreak: { mode: ['avoid-all', 'css', 'legacy'] }
        };

        window.html2pdf().set(opt).from(sheet).save().then(() => {
          btn.disabled = false;
          btn.innerHTML = originalText;
        }).catch(err => {
          console.error('PDF oluşturma hatası:', err);
          alert('PDF oluşturulurken hata oluştu: ' + err.message + '\nYazdırma (Print) penceresi üzerinden "PDF Olarak Kaydet" seçeneğini kullanabilirsiniz.');
          btn.disabled = false;
          btn.innerHTML = originalText;
        });
      } else {
        // html2pdf henüz yüklenmediyse native yazdırma çağrısı
        btn.disabled = false;
        btn.innerHTML = originalText;
        window.print();
      }
    }

    async importVideoMarkersToFindings() {
      // Mevcut hastaya ait videoları filtrele
      const allVideos = (window.capturesData || window.adminCaptures || []).filter(c => c.captureType === 'video');
      const curPatientId = (this.currentData?.patientIdentifier || '').trim().toLowerCase();
      const curPatientName = (this.currentData?.patientName || '').trim().toLowerCase();

      let videos = allVideos;
      if (curPatientId) {
        const pVideos = allVideos.filter(v => (v.patientIdentifier || '').trim().toLowerCase() === curPatientId);
        if (pVideos.length > 0) videos = pVideos;
        else if (curPatientName) {
          const nVideos = allVideos.filter(v => (v.patientName || '').trim().toLowerCase() === curPatientName);
          if (nVideos.length > 0) videos = nVideos;
        }
      } else if (curPatientName) {
        const nVideos = allVideos.filter(v => (v.patientName || '').trim().toLowerCase() === curPatientName);
        if (nVideos.length > 0) videos = nVideos;
      }

      if (videos.length === 0) {
        alert('Bu hasta için aktarılacak video kaydı bulunamadı.');
        return;
      }

      const btn = document.getElementById('btn-import-video-markers');
      if (btn) btn.textContent = 'Aktarılıyor…';

      try {
        const markerLines = [];
        for (const v of videos) {
          const res = await fetch(`/api/captures/${v.id}/markers`);
          if (res.ok) {
            const markers = await res.json();
            if (markers.length > 0) {
              const videoTitle = `Video #${v.id} (${v.roomName || 'Oda'})`;
              markerLines.push(`[${videoTitle} - Zaman Damgalı Bulgular]:`);
              markers.forEach(m => {
                const totalSec = Math.round(m.timestampMs / 1000);
                const min = String(Math.floor(totalSec / 60)).padStart(2, '0');
                const sec = String(totalSec % 60).padStart(2, '0');
                markerLines.push(`  - [${min}:${sec}] ${m.label}`);
              });
            }
          }
        }

        if (markerLines.length === 0) {
          alert('Videolarda henüz kayıtlı bir işaret/marker bulunamadı.');
          if (btn) btn.innerHTML = `<svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M19 21l-7-5-7 5V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2z"/></svg> Video İşaretlerini Aktar`;
          return;
        }

        const findingsTextarea = document.getElementById('rep-inp-findings');
        const existingVal = findingsTextarea.value.trim();
        const appendText = markerLines.join('\n');

        findingsTextarea.value = existingVal ? `${existingVal}\n\n${appendText}` : appendText;

        const previewFindings = document.getElementById('sheet-preview-findings');
        if (previewFindings) previewFindings.textContent = findingsTextarea.value;

        if (btn) btn.innerHTML = `<svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M19 21l-7-5-7 5V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2z"/></svg> Aktarıldı (${markerLines.length - 1})`;
      } catch (err) {
        alert('İşaretler aktarılırken hata oluştu: ' + err.message);
        if (btn) btn.innerHTML = `<svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M19 21l-7-5-7 5V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2z"/></svg> Video İşaretlerini Aktar`;
      }
    }

    printReport() {
      window.print();
    }

    close() {
      const overlay = document.getElementById('report-modal-overlay');
      if (overlay) {
        overlay.classList.remove('active');
      }
    }
  }

  // Global singleton olarak tanımla
  window.MedicalReport = new MedicalReportEngine();
})();
