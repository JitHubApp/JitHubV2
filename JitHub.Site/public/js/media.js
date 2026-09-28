(() => {
    const revealImage = (image) => {
        if (window.JitHubThemeMedia) {
            window.JitHubThemeMedia.load(image);
            return;
        }

        const theme = document.documentElement.dataset.theme === "dark" ? "dark" : "light";
        const source = theme === "dark" ? image.dataset.themeDarkSrc : image.dataset.themeLightSrc;
        image.dataset.mediaVisible = "true";
        if (source) {
            image.setAttribute("src", source);
        }
    };

    const imageObserver = "IntersectionObserver" in window
        ? new IntersectionObserver((entries, observer) => {
            for (const entry of entries) {
                if (!entry.isIntersecting) {
                    continue;
                }

                revealImage(entry.target);
                observer.unobserve(entry.target);
            }
        }, { rootMargin: "20px 0px" })
        : null;

    const registerImage = (image) => {
        if (image.dataset.mediaRegistered === "true") {
            return;
        }

        image.dataset.mediaRegistered = "true";
        if (image.dataset.themeImmediate === "true" || !imageObserver) {
            revealImage(image);
            return;
        }

        imageObserver.observe(image);
    };

    const registerThemeMedia = () => {
        document.querySelectorAll("[data-theme-media]").forEach(registerImage);
    };

    registerThemeMedia();
    document.addEventListener("enhancedload", registerThemeMedia);
})();
