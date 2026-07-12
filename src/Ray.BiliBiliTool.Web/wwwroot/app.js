(() => {
    let lastDragPoint = null;

    document.addEventListener("dragover", (event) => {
        lastDragPoint = {
            clientX: event.clientX,
            clientY: event.clientY
        };
    }, true);

    window.bibiWorkflow = {
        getCanvasDropPoint(clientX, clientY, zoomScale) {
            const stage = document.querySelector(".workflow-canvas-stage");
            if (!stage) return null;

            let x = Number(clientX);
            let y = Number(clientY);
            if ((!x && !y) && lastDragPoint) {
                x = lastDragPoint.clientX;
                y = lastDragPoint.clientY;
            }

            const rect = stage.getBoundingClientRect();
            if (x < rect.left || x > rect.right || y < rect.top || y > rect.bottom) {
                return null;
            }

            const scale = Math.max(0.5, Number(zoomScale) || 1);
            return {
                x: Math.max(24, Math.round((x - rect.left + stage.scrollLeft) / scale) - 110),
                y: Math.max(24, Math.round((y - rect.top + stage.scrollTop) / scale) - 44)
            };
        }
    };
})();
