// Petites fonctions appelées depuis Blazor
window.parcInfo = {
    /** Fait défiler jusqu'à la première question en erreur et place le curseur dans son champ. */
    allerPremiereErreur: function () {
        const q = document.querySelector('.question-erreur');
        if (!q) return;
        // Décalage pour ne pas passer sous la barre du haut (fixe)
        const haut = q.getBoundingClientRect().top + window.scrollY - 90;
        window.scrollTo({ top: haut, behavior: 'smooth' });
        const champ = q.querySelector('input:not([type=checkbox]):not([type=radio]), textarea');
        if (champ) champ.focus({ preventScroll: true });
    }
};
