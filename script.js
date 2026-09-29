let scene, camera, renderer;
let tableCard, opponentCardDeck;
let timeLeft = 60;
let playerWeight = 50;
let gameActive = true;
let timerInterval = null;

const timeDisplay = document.getElementById('time-display');
const scaleBar = document.getElementById('scale-bar');
const weightValueLabel = document.getElementById('weight-value');
const statusText = document.getElementById('status-text');
const btnPlayCard = document.getElementById('btn-play-card');
const btnRestart = document.getElementById('btn-restart');

function init3D() {
    const container = document.getElementById('canvas-container');

    // Create Scene
    scene = new THREE.Scene();
    scene.background = new THREE.Color(0x050508);
    scene.fog = new THREE.FogExp2(0x050508, 0.08);

    // Create Camera (1st person seated perspective angle)
    camera = new THREE.PerspectiveCamera(60, window.innerWidth / window.innerHeight, 0.1, 1000);
    camera.position.set(0, 4.5, 5);
    camera.rotation.x = -Math.PI / 6;

    // Create WebGL Renderer
    renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setSize(window.innerWidth, window.innerHeight);
    renderer.shadowMap.enabled = true;
    container.appendChild(renderer.domElement);

    // Lighting
    const ambientLight = new THREE.AmbientLight(0xffffff, 0.4);
    scene.add(ambientLight);

    const cyanLight = new THREE.PointLight(0x00f0ff, 2, 20);
    cyanLight.position.set(0, 5, 2);
    scene.add(cyanLight);

    const orangeLight = new THREE.PointLight(0xff8800, 2, 20);
    orangeLight.position.set(0, 3, -4);
    scene.add(orangeLight);

    // Create 3D Table Surface
    const tableGeometry = new THREE.BoxGeometry(10, 0.5, 8);
    const tableMaterial = new THREE.MeshStandardMaterial({ 
        color: 0x0f0f1a, 
        roughness: 0.2, 
        metalness: 0.8 
    });
    const table = new THREE.Mesh(tableGeometry, tableMaterial);
    table.position.set(0, -0.25, 0);
    scene.add(table);

    // Table Edge Neon Trim Strip
    const edgeGeometry = new THREE.BoxGeometry(10.2, 0.1, 8.2);
    const edgeMaterial = new THREE.MeshBasicMaterial({ color: 0x00f0ff });
    const tableEdge = new THREE.Mesh(edgeGeometry, edgeMaterial);
    tableEdge.position.set(0, -0.05, 0);
    scene.add(tableEdge);

    // Create Playable Card on Table
    const cardGeometry = new THREE.BoxGeometry(1.4, 0.02, 2);
    const cardMaterial = new THREE.MeshStandardMaterial({ color: 0x1a1a2e, roughness: 0.5 });
    tableCard = new THREE.Mesh(cardGeometry, cardMaterial);
    tableCard.position.set(0, 0.05, 0);
    scene.add(tableCard);

    // Window Resize Handler
    window.addEventListener('resize', onWindowResize);
    
    // Start Animation Loop
    animate();
}

function animate() {
    requestAnimationFrame(animate);

    // Subtle floating animation for the active card on the table
    if (tableCard && gameActive) {
        tableCard.rotation.y += 0.005;
    }

    renderer.render(scene, camera);
}

function onWindowResize() {
    camera.aspect = window.innerWidth / window.innerHeight;
    camera.updateProjectionMatrix();
    renderer.setSize(window.innerWidth, window.innerHeight);
}

function startGame() {
    timeLeft = 60;
    playerWeight = 50;
    gameActive = true;
    
    timeDisplay.textContent = `${timeLeft}s`;
    weightValueLabel.textContent = `Table Weight: ${playerWeight} / 100`;
    scaleBar.style.width = `${playerWeight}%`;
    statusText.textContent = "Table active. Play your hand or get overloaded!";
    
    btnPlayCard.style.display = "block";
    btnPlayCard.disabled = false;
    btnRestart.style.display = "none";

    clearInterval(timerInterval);
    timerInterval = setInterval(() => {
        if (!gameActive) return;

        timeLeft--;
        timeDisplay.textContent = `${timeLeft}s`;

        if (timeLeft <= 0) {
            endGame("Time's Up! Match Over.");
        }
    }, 1000);
}

function playCard() {
    if (!gameActive) return;

    playerWeight += 10;
    if (playerWeight > 100) playerWeight = 100;

    weightValueLabel.textContent = `Table Weight: ${playerWeight} / 100`;
    scaleBar.style.width = `${playerWeight}%`;

    // Quick bounce scale animation effect on the 3D card
    if (tableCard) {
        tableCard.scale.set(1.2, 1.5, 1.2);
        setTimeout(() => tableCard.scale.set(1, 1, 1), 200);
    }

    if (playerWeight >= 100) {
        endGame("OVERLOAD! Too much weight on your side. Defeat!");
    }
}

function endGame(message) {
    gameActive = false;
    clearInterval(timerInterval);
    statusText.textContent = message;
    btnPlayCard.disabled = true;
    btnRestart.style.display = "block";
}

btnPlayCard.addEventListener('click', playCard);
btnRestart.addEventListener('click', startGame);

// Initialize engine on window load
window.addEventListener('DOMContentLoaded', () => {
    init3D();
    startGame();
});
