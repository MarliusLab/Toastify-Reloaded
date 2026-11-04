# Parità permanente Windows, Linux e macOS

Ogni modifica funzionale Windows deve essere valutata e portata anche su Linux e macOS nello stesso ciclo di lavoro: navigazione Spotify, scorciatoie, impostazioni, rilevamento versioni, manutenzione e aggiornamenti. Una differenza imposta dal sistema operativo richiede un equivalente nativo documentato e una prova appropriata. Una limitazione ancora aperta resta dichiarata; il solo successo della compilazione non la risolve.

Le versioni pubblicate in questo ciclo sono Windows `1.3.14`, Linux `1.4.4-linux` e macOS `1.5.0-macos-rc.5`, tutte dal commit `94e5cdb45086ecc9471c3739426ac9b369019eab` dopo tre build main riusciti. Il piano di pubblicazione è [release-plan.json](releases/release-plan.json); [stato e prove](PLATFORM_STATUS.md) riporta i workflow effettivi. I 34 tag precedenti sono rimasti invariati.

## Comportamento comune

- Clic su titolo, artista, copertina o sfondo del toast apre il percorso fisso `spotify:app:lyrics-plus`. Invio/Spazio sui controlli del testo offre la stessa azione. Ctrl+trascinamento mantiene lo spostamento del toast.
- La scorciatoia **Show Toast** salvata mostra un toast alla prima pressione. Se almeno un toast è ancora visibile, la pressione successiva richiede Lyrics Plus; dopo la chiusura di tutti i toast torna a mostrarne uno. Il controller comune gestisce anche più toast e richieste concorrenti, senza riscrivere le preferenze.
- La pressione successiva può essere ritardata: non serve un doppio clic rapido e non esiste un intervallo massimo fra le pressioni. Conta la visibilità del toast in quel momento; la durata impostata dall'utente rimane invariata.
- Per Show Toast puoi mantenere Ctrl o altri modificatori fra le pressioni: il percorso dedicato attende il rilascio del tasto principale e del mouse prima di aprire l'URI fisso. Clic/Invio e Now Playing mantengono i propri controlli più rigidi. Un toast ancora visibile durante la dissolvenza può interrompere la vecchia chiusura, restare attivo durante la navigazione e riprendere l'uscita dopo l'azione.
- `Alt+Shift+J` conserva l'azione del brano corrente mentre il toast è visibile, con precedenza rispetto a un eventuale binding utente dello stesso accordo. Il percorso Lyrics non invia comandi di riproduzione né usa titolo/artista per costruire URI.
- Spotify e Spicetify vengono rilevati con controlli indipendenti e limitati nel tempo. La manutenzione preserva le impostazioni esistenti e impedisce esecuzioni sovrapposte.
- Gli aggiornamenti mostrano avanzamento e verifica, consentono l'annullamento prima della consegna all'installer e distinguono la richiesta di autorizzazione dal download. Dopo la consegna non vengono interrotti arbitrariamente i gestori di pacchetti. Il riavvio dell'updater sopprime un nuovo tentativo automatico nella stessa sessione senza cambiare le preferenze salvate.

L'accettazione dell'URI dal sistema operativo conferma soltanto la consegna al client. Lyrics Plus deve essere installato e applicato in Spicetify; disponibilità dei testi, provider e risultato visivo dipendono dal client e dal brano. Il clic non abilita o ripara automaticamente Spicetify.

## Differenze native dichiarate

| Area | Windows | Linux | macOS |
|---|---|---|---|
| Navigazione Lyrics Plus | Protocollo Spotify, controlli di rilascio dei tasti e attivazione del client | `xdg-open` con URI fisso; ripiego sicuro sull'attivazione MPRIS/Spotify | `open -a Spotify` con URI fisso e controlli di rilascio dei tasti |
| `Alt+Shift+J` | Richiesta Now Playing con proprietà temporanea dell'accordo; nessun inoltro a un proprietario esterno | Su X11 binding temporaneo mentre un toast è visibile; URI del brano risolto nuovamente da MPRIS. Su Wayland l'accordo resta locale al toast con focus | Hook globale con toast visibile e permesso Accessibilità; richiesta Now Playing mediante Automation |
| Show Toast globale | Binding configurato dell'utente | X11 oppure portale Global Shortcuts su Wayland, quando supportato e autorizzato dal compositor | Hook nativo, subordinato ai permessi di sistema |
| Installazione aggiornamenti | Velopack per installazioni migrate; percorso NSIS legacy mantenuto | Velopack per le nuove AppImage; DEB e tar mantengono il proprio percorso nativo | Velopack per i bundle predisposti; percorso ZIP legacy mantenuto |

L'apertura del current-track URI Linux è l'equivalente disponibile del comando di navigazione: Spotify decide il comportamento del collegamento. Non è una prova di continuità della riproduzione o di assenza di riavvio del brano. La seconda pressione Show Toast rimane utilizzabile per Lyrics Plus anche su Wayland dove il portale consente la scorciatoia configurata. Se Show Toast è configurato proprio come Alt+Shift+J, con un toast visibile prevale invece il brano corrente. Un accordo rimappato esternamente dal portale può differire dalla preferenza salvata: l'app non può rilevare tutte le rimappature del compositor.

Su macOS Accessibilità e Automation sono permessi distinti. Build e test con servizi simulati non concedono tali permessi e non provano il risultato nell'interfaccia reale di Spotify. La RC non diventa una stable per effetto di questi controlli; firma Developer ID, notarizzazione e accettazione interattiva restano evidenze separate da raccogliere.

## Pacchetti completi e delta

Tutti i progetti usano SDK/CLI Velopack `1.2.161`, canali separati per piattaforma e architettura e pacchetti con identità verificata. Linux `1.4.2` e macOS `1.5.0-rc.3` sono le prime basi pubbliche native: contengono pacchetti **full**, feed e checksum per entrambe le architetture. Non esiste una precedente base pubblica Velopack Unix da cui produrre delta reali per queste prime release.

Le release successive possono generare delta dalla precedente base full immutabile. Anche con un delta pubblicato, una nuova installazione priva del full precedente nella cache può richiedere il pacchetto completo al primo aggiornamento. Il backend ricorre al full quando la ricostruzione delta fallisce; metadati, architettura e contenuti vengono verificati prima della consegna al helper nativo.

Linux 1.4.3 e macOS RC4 pubblicano ora due delta reali ciascuno dalle basi 1.4.2/RC3. La CI ha ricostruito i pacchetti con gli helper ufficiali su Linux x64/ARM64 e macOS ARM64, confrontato tutti i contenuti e rifiutato delta corrotti, senza installare o avviare l'applicazione. Le prove dei delta non sostituiscono l'accettazione di un aggiornamento installato.

Le AppImage Linux restano AppImage. Un'installazione DEB continua a usare il gestore di pacchetti e un'installazione tar conserva il percorso di aggiornamento portabile/manuale: non vengono convertite silenziosamente in installazioni Velopack. I download ZIP/DMG macOS restano disponibili. Impostazioni e identità di installazione esistenti non vengono azzerate per abilitare i feed nuovi.

## Evidenze richieste per ogni cambiamento

`python scripts/verify-platform-parity.py` verifica XML dei progetti, collegamento delle sorgenti comuni, SDK fissato, integrazione dei contratti UI/manutenzione e comportamento del gate di pubblicazione con risposte GitHub simulate. Richiama inoltre il confronto strutturale di impostazioni e XAML Linux/macOS. È un controllo di integrazione statica con prove sintetiche della provenienza, non un test di interazione nativa.

Il harness condiviso verifica controller Show Toast, gate, URI/fallback, processi e download limitati, annullamento e backend dei pacchetti mediante dipendenze controllate. Il harness Windows aggiunge WPF, input e updater; le prove patch-only usano helper ufficiali su dati isolati senza installare l'applicazione. I test e i conteggi effettivamente riusciti vanno riportati con il relativo commit e ambiente.

Le tre pipeline principali vengono avviate per ogni push e pull request su `main`, senza filtri che escludano una piattaforma. Ogni piattaforma compila e confeziona i propri pacchetti; smoke test e self-test nativi restano nei build. Il gate dei tre workflow di rilascio richiede **tutti e tre i build riusciti dello stesso SHA**, provenienti da push `main` del repository atteso, prima di consentire pubblicazioni. I workflow scaricano gli artifact di quei run precisi e non ricostruiscono i binari.

I pacchetti vengono caricati in draft private, verificati integralmente e poi pubblicati. Tag e release precedenti rimangono immutabili; solo Windows stable viene candidato a Latest, mentre Linux e macOS mantengono i propri canali. Questo coordina i prerequisiti delle tre pubblicazioni; GitHub non offre una transazione atomica fra tre release, quindi un errore di caricamento/pubblicazione richiede recupero della draft originale e una comunicazione esplicita dello stato.

Per dichiarare completa una funzione servono anche le prove pertinenti sulla piattaforma reale: client Spotify/Spicetify, X11 o compositor Wayland, permessi macOS, scorciatoie personalizzate, riproduzione attiva e conservazione delle impostazioni. Gli smoke test GUI dei runner verificano l'avvio; non sostituiscono queste prove. In questo ciclo il port Linux/macOS non è ancora stato accettato interattivamente su un desktop reale con Spotify: tale limite va mantenuto nel riepilogo finché non viene verificato.
