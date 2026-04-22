# Web UI Development Guidance

## Accessibility (WCAG 2.1 AA)

### Accessibility Standards
Follow WCAG 2.1 Level AA compliance for web content. Support keyboard navigation for all interactive elements. Use proper semantic HTML to convey meaning and structure. Test with assistive technologies (screen readers, keyboard navigation). Include alt text for all meaningful images. Validate accessibility with automated tools and manual testing.

### ARIA Roles and Attributes
Use ARIA only when native HTML doesn't provide semantics. Apply roles like `button`, `navigation`, `main` to enhance structure. Use `aria-label` and `aria-labelledby` for unlabeled controls. Use `aria-live` for dynamic content updates. Use `aria-describedby` for additional descriptions. Keep ARIA attributes in sync with DOM state.

### Keyboard Navigation
Support Tab key for navigation between focusable elements. Define logical tab order matching visual flow. Implement keyboard shortcuts with proper key combinations. Provide visual focus indicators that are clearly visible. Support Escape key for closing modals and menus. Test keyboard navigation with various screen reader combinations.

## Responsive Design

### Mobile-First Approach
Start with mobile layout and enhance for larger screens. Design for touch-friendly interfaces with adequate tap targets (48px minimum). Optimize performance for mobile networks. Progressively enhance with features for desktop. Use mobile-first CSS media queries with `min-width` breakpoints.

### Breakpoints and Media Queries
Define standard breakpoints: mobile (320px), tablet (768px), desktop (1024px), large (1440px). Adjust layouts and font sizes at breakpoints. Test at actual device sizes and orientation changes. Use CSS Grid and Flexbox for flexible layouts. Support landscape and portrait orientations.

### Viewport Configuration
Set proper viewport meta tag for responsive behavior. Use `viewport-fit=cover` for notch support on mobile. Ensure content scales appropriately. Support user zoom without breaking layout. Avoid fixed viewport widths that prevent responsive scaling.

## Semantic HTML

### HTML Structure
Use semantic elements (`header`, `nav`, `main`, `article`, `section`, `footer`) for document structure. Use heading hierarchy (`h1` to `h6`) properly without skipping levels. Use `<form>` elements for form submission. Use `<label>` for form inputs. Use `<button>` for clickable actions instead of `<div>` styled as buttons.

### Data and Lists
Use `<table>` for tabular data with `<th>` for headers. Use `<ul>` for unordered lists and `<ol>` for ordered lists. Use `<dl>`, `<dt>`, `<dd>` for definition lists. Use `<fieldset>` and `<legend>` for grouped form controls. Use `<figure>` and `<figcaption>` for images with captions.

## CSS Organization

### CSS Architecture
Organize CSS into logical sections: reset/normalize, base styles, layout, components, utilities. Use CSS custom properties (variables) for colors, spacing, and typography. Implement a consistent naming convention (BEM or similar). Keep specificity low to avoid cascading issues. Use CSS Grid for page layouts and Flexbox for component layouts.

### Scalable Styling
Create reusable component styles with minimal dependencies. Avoid deeply nested selectors. Use utility classes for common patterns (margins, padding, alignment). Document color, typography, and spacing systems. Support theming through CSS variables for light/dark modes. Keep stylesheet file sizes reasonable by splitting large files.

## Component Patterns

### Reusable Components
Build modular components with single responsibility. Define clear props/attributes for customization. Support composition for building complex UIs. Document component usage with examples. Version components with clear changelog. Support accessibility requirements within each component.

### State Management
Manage component state clearly and predictably. Use appropriate state management for UI complexity (useState, Redux, etc.). Lift state when multiple components need the same data. Avoid prop drilling with context or state management libraries. Document state flows and data dependencies.

## Performance Optimization

### Lazy Loading
Implement lazy loading for images with intersection observers or native `loading="lazy"`. Load images as users scroll. Support responsive images with `srcset` for different screen sizes. Consider compression and modern formats (WebP). Measure and monitor Core Web Vitals.

### Code Splitting
Split JavaScript bundles by route or feature. Load code on demand to reduce initial bundle size. Support dynamic imports with fallbacks. Monitor bundle size over time. Use tree-shaking to eliminate unused code. Prioritize critical rendering path.

### General Performance
Minimize render cycles and reflows. Debounce scroll and resize events. Use CSS transforms for animations instead of layout properties. Cache computed values to avoid recalculation. Profile performance with browser DevTools. Aim for fast Largest Contentful Paint (LCP) and low Cumulative Layout Shift (CLS).
