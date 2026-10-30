// Comportements de saisie communs à tous les écrans (charte NetPrint, repris du WinDev).
// Chargé une fois par App.razor ; les écouteurs sont posés sur le document, ils valent donc pour tous les
// champs, y compris ceux affichés plus tard (bulles de filtre, fenêtres).
(() => {
    // Double-clic dans une zone date : date du jour.
    // On prévient Blazor par les événements « input » et « change » (@bind et saisie au fil de l'eau).
    document.addEventListener('dblclick', (e) => {
        const champ = e.target;
        if (!(champ instanceof HTMLInputElement) || champ.type !== 'date' || champ.disabled || champ.readOnly) return;
        const j = new Date();
        champ.value = `${j.getFullYear()}-${String(j.getMonth() + 1).padStart(2, '0')}-${String(j.getDate()).padStart(2, '0')}`;
        champ.dispatchEvent(new Event('input', { bubbles: true }));
        champ.dispatchEvent(new Event('change', { bubbles: true }));
    });

    // Glisser-déposer de fichiers : seules les zones prévues (champ fichier, ex. zone de dépôt des documents)
    // acceptent un fichier. Ailleurs, rien ne se passe (sinon le navigateur ouvre le fichier à la place de l'appli).
    const zoneFichier = (el) => el instanceof Element && el.closest("input[type='file']") !== null;
    for (const evenement of ['dragover', 'drop']) {
        document.addEventListener(evenement, (e) => {
            if (zoneFichier(e.target)) return;
            e.preventDefault();
            if (e.dataTransfer) e.dataTransfer.dropEffect = 'none';
        });
    }

    // Fenêtres internes (texte libre, sous-totaux, documents, échec, tags, dialogues…) : déplaçables en tirant
    // leur barre de titre. La fenêtre reste dans l'écran ; elle reprend sa place centrée à sa prochaine ouverture.
    document.addEventListener('mousedown', (e) => {
        if (e.button !== 0 || !(e.target instanceof Element)) return;
        const titre = e.target.closest('.voile .fenetre > .titre-fenetre');
        if (!titre) return;
        const fenetre = titre.parentElement;
        e.preventDefault();
        const depart = { x: e.clientX, y: e.clientY };
        const decalage = { x: Number(fenetre.dataset.dx || 0), y: Number(fenetre.dataset.dy || 0) };
        const r = fenetre.getBoundingClientRect();
        let bouge = false;

        const bouger = (m) => {
            let dx = m.clientX - depart.x, dy = m.clientY - depart.y;
            if (!bouge && Math.abs(dx) + Math.abs(dy) < 3) return;
            bouge = true;
            // Limites : la barre de titre reste visible
            dx = Math.min(Math.max(dx, -r.left), window.innerWidth - r.right);
            dy = Math.min(Math.max(dy, -r.top), window.innerHeight - r.top - 40);
            fenetre.dataset.dx = decalage.x + dx;
            fenetre.dataset.dy = decalage.y + dy;
            fenetre.style.transform = `translate(${decalage.x + dx}px, ${decalage.y + dy}px)`;
        };
        const fin = () => {
            document.removeEventListener('mousemove', bouger);
            document.removeEventListener('mouseup', fin);
            document.body.classList.remove('deplacement');
            if (!bouge) return;
            // Le relâchement au-dessus du voile ne doit pas fermer la fenêtre
            const bloquer = (c) => { c.stopPropagation(); c.preventDefault(); };
            window.addEventListener('click', bloquer, true);
            setTimeout(() => window.removeEventListener('click', bloquer, true), 0);
        };
        document.body.classList.add('deplacement');
        document.addEventListener('mousemove', bouger);
        document.addEventListener('mouseup', fin);
    });

    // F4 dans une zone de saisie : ouvre la recherche (bouton « … » du même champ), comme dans le WinDev
    document.addEventListener('keydown', (e) => {
        if (e.key !== 'F4' || !(e.target instanceof HTMLInputElement)) return;
        const bouton = e.target.closest('.champ, .ligne-champs')?.querySelector('.bouton-liste:not(:disabled)');
        if (!bouton) return;
        e.preventDefault();
        bouton.click();
    });

    // Zones numériques : ni flèches (masquées par app.css), ni molette, ni touches haut / bas pour changer la valeur
    const estNombre = (el) => el instanceof HTMLInputElement && el.type === 'number';
    document.addEventListener('wheel', (e) => {
        if (estNombre(e.target) && document.activeElement === e.target) e.preventDefault();
    }, { passive: false });
    document.addEventListener('keydown', (e) => {
        if (estNombre(e.target) && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) e.preventDefault();
    });
})();
