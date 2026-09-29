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

function startGame() {
    timeLeft = 60;
    playerWeight = 50;
    gameActive = true;
    
    timeDisplay.textContent = `${timeLeft}s`;
    weightValueLabel.textContent = `Weight: ${playerWeight} / 100`;
    scaleBar.style.width = `${playerWeight}%`;
    statusText.textContent = "Dump your weight before time runs out!";
    
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

    weightValueLabel.textContent = `Weight: ${playerWeight} / 100`;
    scaleBar.style.width = `${playerWeight}%`;

    if (playerWeight >= 100) {
        endGame("OVERLOAD! You accumulated too much weight and lost!");
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

// Initialize on load
startGame();
