// Grilles NetPrint : largeur des colonnes réglée à la souris, position des bulles sous les titres.
// Rien n'est stocké dans le navigateur : la largeur choisie est renvoyée au serveur (FinRedimension).

// Rectangle d'un élément à l'écran (pour placer une bulle sous un titre de colonne)
export function rectangle(element) {
    const r = element.getBoundingClientRect();
    return { gauche: r.left, haut: r.top, bas: r.bottom, droite: r.right, largeurFenetre: window.innerWidth };
}

// Branche les poignées (.poignee-colonne[data-cle]) d'une table : tirer = élargir, double-clic = largeur d'origine.
// Les colonnes de la table ont leur largeur sur <col data-cle="...">.
export function activer(table, dotnet) {
    if (!table || table.dataset.redimension === 'oui') return;
    table.dataset.redimension = 'oui';

    // Un clic sur une poignée ne doit ni trier ni ouvrir la bulle du titre
    table.addEventListener('click', (e) => {
        if (e.target.closest('.poignee-colonne')) e.stopPropagation();
    });

    table.addEventListener('dblclick', (e) => {
        const poignee = e.target.closest('.poignee-colonne');
        if (!poignee) return;
        e.stopPropagation();
        dotnet.invokeMethodAsync('FinRedimension', poignee.dataset.cle, 0);
    });

    table.addEventListener('mousedown', (e) => {
        const poignee = e.target.closest('.poignee-colonne');
        if (!poignee || e.button !== 0) return;
        e.preventDefault();
        e.stopPropagation();
        const cle = poignee.dataset.cle;
        const th = poignee.closest('th');
        const col = table.querySelector(`col[data-cle="${cle}"]`);
        if (!th || !col) return;

        const depart = e.clientX;
        const initiale = th.getBoundingClientRect().width;
        const tableInitiale = table.getBoundingClientRect().width;
        let largeur = Math.round(initiale);
        let bouge = false;

        const bouger = (m) => {
            bouge = true;
            largeur = Math.max(50, Math.round(initiale + m.clientX - depart));
            col.style.width = largeur + 'px';
            table.style.width = Math.round(tableInitiale - initiale + largeur) + 'px';
        };
        const fin = () => {
            document.removeEventListener('mousemove', bouger);
            document.removeEventListener('mouseup', fin);
            document.body.classList.remove('redimension');
            if (!bouge) return;
            // Le clic qui suit le relâchement ne doit pas ouvrir la bulle du titre
            const bloquer = (c) => { c.stopPropagation(); c.preventDefault(); };
            window.addEventListener('click', bloquer, true);
            setTimeout(() => window.removeEventListener('click', bloquer, true), 0);
            dotnet.invokeMethodAsync('FinRedimension', cle, largeur);
        };
        document.body.classList.add('redimension');
        document.addEventListener('mousemove', bouger);
        document.addEventListener('mouseup', fin);
    });
}
