// Fonctions JavaScript pour le diaporama

window.slideshowInterop = {
    /**
     * Trouve l'index de l'image qui est le plus proche du centre de l'écran
     * @returns {number} L'index de l'image centrale, ou -1 si aucune image trouvée
     */
    getCenterImageIndex: function () {
        const container = document.querySelector('.slideshow-container');
        if (!container) {
            console.warn('Container .slideshow-container non trouvé');
            return -1;
        }

        const imageItems = document.querySelectorAll('.image-item');
        if (imageItems.length === 0) {
            console.warn('Aucune image trouvée');
            return -1;
        }

        // Centre de l'écran
        const screenCenterX = window.innerWidth / 2;
        const screenCenterY = window.innerHeight / 2;

        let closestIndex = -1;
        let closestDistance = Infinity;

        imageItems.forEach((item, index) => {
            // Obtenir la position réelle de l'élément (incluant les transformations CSS)
            const rect = item.getBoundingClientRect();
            
            // Centre de l'image
            const itemCenterX = rect.left + rect.width / 2;
            const itemCenterY = rect.top + rect.height / 2;

            // Distance au centre de l'écran
            const distance = Math.sqrt(
                Math.pow(itemCenterX - screenCenterX, 2) +
                Math.pow(itemCenterY - screenCenterY, 2)
            );

            if (distance < closestDistance) {
                closestDistance = distance;
                closestIndex = index;
            }
        });

        console.log(`?? Image centrale détectée: index ${closestIndex}, distance: ${closestDistance.toFixed(0)}px`);
        return closestIndex;
    },

    /**
     * Obtient les informations de l'image au centre
     * @returns {object} Informations sur l'image centrale
     */
    getCenterImageInfo: function () {
        const index = this.getCenterImageIndex();
        if (index === -1) {
            return { index: -1, found: false };
        }

        const imageItems = document.querySelectorAll('.image-item');
        const item = imageItems[index];
        const rect = item.getBoundingClientRect();

        return {
            index: index,
            found: true,
            centerX: rect.left + rect.width / 2,
            centerY: rect.top + rect.height / 2,
            width: rect.width,
            height: rect.height
        };
    }
};
